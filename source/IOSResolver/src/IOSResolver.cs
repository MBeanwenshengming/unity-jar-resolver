﻿// <copyright file="VersionHandler.cs" company="Google Inc.">
// Copyright (C) 2016 Google Inc. All Rights Reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//    limitations under the License.
// </copyright>

#if UNITY_IOS
using GooglePlayServices;
using Google.JarResolver;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor.iOS.Xcode;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;

namespace Google {

public static class IOSResolver {
    /// <summary>
    /// Reference to a Cocoapod.
    /// </summary>
    private class Pod {
        /// <summary>
        /// Name of the pod.
        /// </summary>
        public string name = null;

        /// <summary>
        /// Version specification string.
        /// If it ends with "+" the specified version up to the next major
        /// version is selected.
        /// If "LATEST", null or empty this pulls the latest revision.
        /// A version number "1.2.3" selects a specific version number.
        /// </summary>
        public string version = null;

        /// <summary>
        /// Whether this pod has been compiled with bitcode enabled.
        ///
        /// If any pods are present which have bitcode disabled, bitcode is
        /// disabled for an entire project.
        /// </summary>
        public bool bitcodeEnabled = true;

        /// <summary>
        /// Minimum target SDK revision required by this pod.
        /// In the form major.minor
        /// </summary>
        public string minTargetSdk = null;

        /// <summary>
        /// Format a "pod" line for a Podfile.
        /// </summary>
        public string PodFilePodLine {
            get {
                string versionExpression = "";
                if (!String.IsNullOrEmpty(version) &&
                    !version.Equals("LATEST")) {
                    if (version.EndsWith("+")) {
                        versionExpression = String.Format(
                            ", '~> {0}'",
                            version.Substring(0, version.Length - 1));
                    } else {
                        versionExpression = String.Format(", '{0}'", version);
                    }
                }
                return String.Format("pod '{0}'{1}", name, versionExpression);
            }
        }

        /// <summary>
        /// Create a pod reference.
        /// </summary>
        /// <param name="name">Name of the pod.</param>
        /// <param name="version">Version of the pod.</param>
        /// <param name="bitcodeEnabled">Whether this pod was compiled with
        /// bitcode.</param>
        /// <param name="minTargetSdk">Minimum target SDK revision required by
        /// this pod.</param>
        public Pod(string name, string version, bool bitcodeEnabled,
                   string minTargetSdk) {
            this.name = name;
            this.version = version;
            this.bitcodeEnabled = bitcodeEnabled;
            this.minTargetSdk = minTargetSdk;
        }

        /// <summary>
        /// Convert min target SDK to an integer in the form
        // (major * 10) + minor.
        /// </summary>
        /// <return>Numeric minimum SDK revision required by this pod.</return>
        public int MinTargetSdkToVersion() {
            string sdkString =
                String.IsNullOrEmpty(minTargetSdk) ? "0.0" : minTargetSdk;
            if (!minTargetSdk.Contains(".")) {
                sdkString = minTargetSdk + ".0";
            }
            return IOSResolver.TargetSdkStringToVersion(sdkString);
        }

        /// <summary>
        /// Given a list of pods bucket them into a dictionary sorted by
        /// min SDK version.  Pods which specify no minimum version (e.g 0)
        /// are ignored.
        /// </summary>
        /// <param name="pods">Enumerable of pods to query.</param>
        /// <returns>Sorted dictionary of lists of pod names bucketed by
        /// minimum required SDK version.</returns>
        public static SortedDictionary<int, List<string>>
                BucketByMinSdkVersion(IEnumerable<Pod> pods) {
            var buckets = new SortedDictionary<int, List<string>>();
            foreach (var pod in pods) {
                int minVersion = pod.MinTargetSdkToVersion();
                if (minVersion == 0) {
                    continue;
                }
                List<string> nameList = null;
                if (!buckets.TryGetValue(minVersion, out nameList)) {
                    nameList = new List<string>();
                }
                nameList.Add(pod.name);
                buckets[minVersion] = nameList;
            }
            return buckets;
        }
    }

    // Dictionary of pods to install in the generated Xcode project.
    private static SortedDictionary<string, Pod> pods =
        new SortedDictionary<string, Pod>();

    // Order of post processing operations.
    private const int BUILD_ORDER_PATCH_PROJECT = 1;
    private const int BUILD_ORDER_GEN_PODFILE = 2;
    private const int BUILD_ORDER_INSTALL_PODS = 3;
    private const int BUILD_ORDER_UPDATE_DEPS = 4;

    // Installation instructions for the Cocoapods command line tool.
    private const string COCOAPOD_INSTALL_INSTRUCTIONS = (
        "You can install cocoapods with the Ruby gem package manager:\n" +
        " > sudo gem install -n /usr/local/bin cocoapods\n" +
        " > pod setup");

    // Paths to search for the "pod" command.
    private static string[] POD_SEARCH_PATHS = new string[] {
        "/usr/local/bin/pod",
        "/usr/bin/pod",
    };

    /// <summary>
    /// Name of the Xcode project generated by Unity.
    /// </summary>
    public const string PROJECT_NAME = "Unity-iPhone";

    /// <summary>
    /// Main executable target of the Xcode project generated by Unity.
    /// </summary>
    public static string TARGET_NAME = null;

    // Keys in the editor preferences which control the behavior of this module.
    private const string PREFERENCE_ENABLED = "Google.IOSResolver.Enabled";

    /// <summary>
    /// Initialize the module.
    /// </summary>
    static IOSResolver() {
        TARGET_NAME = PBXProject.GetUnityTargetName();
    }

    /// <summary>
    /// Enable / disable iOS dependency injection.
    /// </summary>
    public static bool Enabled {
        get { return EditorPrefs.GetBool(PREFERENCE_ENABLED,
                                         defaultValue: true); }
        set { EditorPrefs.SetBool(PREFERENCE_ENABLED, value); }
    }

    /// <summary>
    /// Determine whether a Pod is present in the list of dependencies.
    /// </summary>
    public static bool PodPresent(string pod) {
        return (new List<string>(pods.Keys)).Contains(pod);
    }

    /// <summary>
    /// Whether to inject iOS dependencies in the Unity generated Xcode
    /// project.
    /// </summary>
    private static bool InjectDependencies() {
        return EditorUserBuildSettings.activeBuildTarget == BuildTarget.iOS &&
            Enabled && pods.Count > 0;
    }

    /// <summary>
    /// Tells the app what pod dependencies are needed.
    /// This is called from a deps file in each API to aggregate all of the
    /// dependencies to automate the Podfile generation.
    /// </summary>
    /// <param name="podName">pod path, for example "Google-Mobile-Ads-SDK" to
    /// be included</param>
    /// <param name="version">Version specification.  See Pod.version.</param>
    /// <param name="bitcodeEnabled">Whether the pod was compiled with bitcode
    /// enabled.  If this is set to false on a pod, the entire project will
    /// be configured with bitcode disabled.</param>
    /// <param name="minTargetSdk">Minimum SDK revision required by this
    /// pod.</param>
    public static void AddPod(string podName, string version = null,
                              bool bitcodeEnabled = true,
                              string minTargetSdk = null) {
        var pod = new Pod(podName, version, bitcodeEnabled, minTargetSdk);
        pods[podName] = pod;
        UpdateTargetSdk(pod);
    }

    /// <summary>
    /// Update the iOS target SDK if it's lower than the minimum SDK
    /// version specified by the pod.
    /// </summary>
    /// <param name="pod">Pod to query for the minimum supported version.
    /// </param>
    /// <param name="notifyUser">Whether to write to the log to notify the
    /// user of a build setting change.</param>
    /// <returns>true if the SDK version was changed, false
    /// otherwise.</returns>
    private static bool UpdateTargetSdk(Pod pod,
                                        bool notifyUser = true) {
        int currentVersion = TargetSdkVersion;
        int minVersion = pod.MinTargetSdkToVersion();
        if (currentVersion >= minVersion) {
            return false;
        }
        if (notifyUser) {
            string oldSdk = TargetSdk;
            TargetSdkVersion = minVersion;
            Debug.Log("iOS Target SDK changed from " + oldSdk + " to " +
                      TargetSdk + " required by the " + pod.name + " pod");
        }
        return true;
    }

    /// <summary>
    /// Update the target SDK if it's required.
    /// </summary>
    /// <returns>true if the SDK was updated, false otherwise.</returns>
    public static bool UpdateTargetSdk() {
        var minVersionAndPodNames = TargetSdkNeedsUpdate();
        if (minVersionAndPodNames.Value != null) {
            var minVersionString =
                TargetSdkVersionToString(minVersionAndPodNames.Key);
            var update = EditorUtility.DisplayDialog(
                "Unsupported Target SDK",
                "Target SDK selected in the iOS Player Settings is not " +
                "supported by the Cocoapods included in this project. " +
                "The build will very likely fail. The minimum supported " +
                "version is \"" + minVersionString + "\" " +
                "required by pods (" +
                String.Join(", ", minVersionAndPodNames.Value.ToArray()) +
                ").\n" +
                "Would you like to update the target SDK version?",
                "Yes", cancel: "No");
            if (update) {
                TargetSdkVersion = minVersionAndPodNames.Key;
                string errorString = (
                    "Target SDK has been updated to " + minVersionString +
                    ".  You must restart the " +
                    "build for this change to take effect.");
                EditorUtility.DisplayDialog(
                    "Target SDK updated.", errorString, "OK");
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Determine whether the target SDK needs to be updated based upon pod
    /// dependencies.
    /// </summary>
    /// <returns>Key value pair of minimum SDK version (key) and
    /// a list of pod names that require it (value) if the currently
    /// selected target SDK version does not satify pod requirements, the list
    /// (value) is null otherwise.</returns>
    private static KeyValuePair<int, List<string>> TargetSdkNeedsUpdate() {
        var kvpair = new KeyValuePair<int, List<string>>(0, null);
        var podListsByVersion = Pod.BucketByMinSdkVersion(pods.Values);
        if (podListsByVersion.Count == 0) {
            return kvpair;
        }
        KeyValuePair<int, List<string>> minVersionAndPodName = kvpair;
        foreach (var versionAndPodList in podListsByVersion) {
            minVersionAndPodName = versionAndPodList;
            break;
        }
        int currentVersion = TargetSdkVersion;
        if (currentVersion >= minVersionAndPodName.Key) {
            return kvpair;
        }
        return minVersionAndPodName;
    }

    /// <summary>
    /// Get the generated xcode project path relative to the specified
    /// directory.
    /// </summary>
    /// <param name="relativeTo">Path the project is relative to.</param>
    public static string GetProjectPath(string relativeTo) {
        return Path.Combine(relativeTo,
                            Path.Combine(PROJECT_NAME + ".xcodeproj",
                                         "project.pbxproj"));
    }

    /// <summary>
    /// Get or set the Unity iOS target SDK version string (e.g "7.1")
    /// build setting.
    /// </summary>
    static string TargetSdk {
        get {
            return UnityEditor.PlayerSettings.iOS.targetOSVersion.ToString().
                Replace("iOS_", "").Replace("_", ".");
        }

        set {
            var targetOSVersion = (iOSTargetOSVersion)System.Enum.Parse(
                typeof(iOSTargetOSVersion),
                "iOS_" + value.Replace(".", "_"));
            UnityEditor.PlayerSettings.iOS.targetOSVersion = targetOSVersion;
        }
    }

    /// <summary>
    /// Get or set the Unity iOS target SDK using a version number (e.g 71
    /// is equivalent to "7.1").
    /// </summary>
    static int TargetSdkVersion {
        get { return TargetSdkStringToVersion(TargetSdk); }
        set { TargetSdk = TargetSdkVersionToString(value); }
    }

    /// <summary>
    /// Convert a target SDK string into a value of the form
    // (major * 10) + minor.
    /// </summary>
    /// <returns>Integer representation of the SDK.</returns>
    internal static int TargetSdkStringToVersion(string targetSdk) {
        return Convert.ToInt32(targetSdk.Replace(".", ""));
    }

    /// <summary>
    /// Convert an integer target SDK value into a string.
    /// </summary>
    /// <returns>String version number.</returns>
    internal static string TargetSdkVersionToString(int version) {
        int major = version / 10;
        int minor = version % 10;
        return major.ToString() + "." + minor.ToString();
    }

    /// <summary>
    /// Determine whether any pods need bitcode disabled.
    /// </summary>
    /// <returns>List of pod names with bitcode disabled.</return>
    private static List<string> FindPodsWithBitcodeDisabled() {
        var disabled = new List<string>();
        foreach (var pod in pods.Values) {
            if (!pod.bitcodeEnabled) {
                disabled.Add(pod.name);
            }
        }
        return disabled;
    }

    /// <summary>
    /// Post-processing build step to patch the generated project files.
    /// </summary>
    [PostProcessBuildAttribute(BUILD_ORDER_PATCH_PROJECT)]
    public static void OnPostProcessPatchProject(BuildTarget buildTarget,
                                                 string pathToBuiltProject) {
        if (!InjectDependencies()) return;

        var podsWithoutBitcode = FindPodsWithBitcodeDisabled();
        bool bitcodeDisabled = podsWithoutBitcode.Count > 0;
        if (bitcodeDisabled) {
            UnityEngine.Debug.LogWarning(
                "Bitcode is disabled due to the following Cocoapods (" +
                String.Join(", ", podsWithoutBitcode.ToArray()) + ")");
        }

        // Configure project settings for Cocoapods.
        string pbxprojPath = GetProjectPath(pathToBuiltProject);
        PBXProject project = new PBXProject();
        project.ReadFromString(File.ReadAllText(pbxprojPath));
        string target = project.TargetGuidByName(TARGET_NAME);
        project.SetBuildProperty(target, "CLANG_ENABLE_MODULES", "YES");
        project.AddBuildProperty(target, "OTHER_LDFLAGS", "$(inherited)");
        project.AddBuildProperty(target, "OTHER_CFLAGS", "$(inherited)");
        project.AddBuildProperty(target, "HEADER_SEARCH_PATHS",
                                 "$(inherited)");
        project.SetBuildProperty(target, "FRAMEWORK_SEARCH_PATHS",
                                 "$(inherited)");
        project.AddBuildProperty(target, "FRAMEWORK_SEARCH_PATHS",
                                 "$(PROJECT_DIR)/Frameworks");
        project.AddBuildProperty(target, "OTHER_LDFLAGS", "-ObjC");
        if (bitcodeDisabled) {
            project.AddBuildProperty(target, "ENABLE_BITCODE", "NO");
        }
        File.WriteAllText(pbxprojPath, project.WriteToString());
    }

    /// <summary>
    /// Post-processing build step to generate the podfile for ios.
    /// </summary>
    [PostProcessBuildAttribute(BUILD_ORDER_GEN_PODFILE)]
    public static void OnPostProcessGenPodfile(BuildTarget buildTarget,
                                               string pathToBuiltProject) {
        if (!InjectDependencies()) return;

        using (StreamWriter file =
               new StreamWriter(Path.Combine(pathToBuiltProject, "Podfile"))) {
            file.Write("source 'https://github.com/CocoaPods/Specs.git'\n" +
                "install! 'cocoapods', :integrate_targets => false\n" +
                string.Format("platform :ios, '{0}'\n\n", TargetSdk) +
                "target '" + TARGET_NAME + "' do\n"
            );
            foreach(var pod in pods.Values) {
                file.WriteLine(pod.PodFilePodLine);
            }
            file.WriteLine("end");
        }
    }

    /// <summary>
    /// Find the "pod" tool.
    /// </summary>
    /// <returns>Path to the pod tool if successful, null otherwise.</returns>
    private static string FindPodTool() {
        foreach (string path in POD_SEARCH_PATHS) {
            if (File.Exists(path)) {
                return path;
            }
        }
        return null;
    }

    /// <summary>
    /// Downloads all of the framework dependencies using pods.
    /// </summary>
    [PostProcessBuildAttribute(BUILD_ORDER_INSTALL_PODS)]
    public static void OnPostProcessInstallPods(BuildTarget buildTarget,
                                                string pathToBuiltProject) {
        if (!InjectDependencies()) return;
        if (UpdateTargetSdk()) return;

        string pod_command = FindPodTool();
        if (String.IsNullOrEmpty(pod_command)) {
            UnityEngine.Debug.LogError(
                "'pod' command not found; unable to generate a usable" +
                " Xcode project. " + COCOAPOD_INSTALL_INSTRUCTIONS);
            return;
        }

        // Require at least version 1.0.0
        CommandLine.Result result =
            CommandLine.Run(pod_command, "--version", pathToBuiltProject);
        if (result.exitCode != 0 || result.stdout[0] == '0') {
            Debug.LogError(
               "Error running cocoapods. Please ensure you have at least " +
               "version  1.0.0.  " + COCOAPOD_INSTALL_INSTRUCTIONS);
            return;
        }

        result = CommandLine.Run(
            pod_command, "install", pathToBuiltProject,
            // cocoapods seems to require this, or it spits out a warning.
            envVars: new Dictionary<string,string>() {
                {"LANG", (System.Environment.GetEnvironmentVariable("LANG") ??
                    "en_US.UTF-8").Split('.')[0] + ".UTF-8"}
            });
        if (result.exitCode != 0) {
            Debug.LogError("Pod install failed. See the output below for " +
                           "details.\n\n" + result.stdout + "\n\n" +
                           result.stderr);
            return;
        }
    }

    /// <summary>
    /// Finds the frameworks downloaded by cocoapods in the Pods directory
    /// and adds them to the project.
    /// </summary>
    [PostProcessBuildAttribute(BUILD_ORDER_UPDATE_DEPS)]
    public static void OnPostProcessUpdateProjectDeps(
            BuildTarget buildTarget, string pathToBuiltProject) {
        if (!InjectDependencies()) return;

        // If the Pods directory does not exist, the pod download step
        // failed.
        var podsDir = Path.Combine(pathToBuiltProject, "Pods");
        if (!Directory.Exists(podsDir)) return;

        Directory.CreateDirectory(Path.Combine(pathToBuiltProject,
                                               "Frameworks"));
        Directory.CreateDirectory(Path.Combine(pathToBuiltProject,
                                               "Resources"));

        string pbxprojPath = GetProjectPath(pathToBuiltProject);
        PBXProject project = new PBXProject();
        project.ReadFromString(File.ReadAllText(pbxprojPath));
        string target = project.TargetGuidByName(TARGET_NAME);

        HashSet<string> frameworks = new HashSet<string>();
        HashSet<string> linkFlags = new HashSet<string>();
        foreach (var frameworkFullPath in
                 Directory.GetDirectories(podsDir, "*.framework",
                                          SearchOption.AllDirectories)) {
            string frameworkName = new DirectoryInfo(frameworkFullPath).Name;
            string destFrameworkPath = Path.Combine("Frameworks",
                                                    frameworkName);
            string destFrameworkFullPath = Path.Combine(pathToBuiltProject,
                                                        destFrameworkPath);

            PlayServicesSupport.DeleteExistingFileOrDirectory(
                destFrameworkFullPath);
            Directory.Move(frameworkFullPath, destFrameworkFullPath);
            project.AddFileToBuild(target,
                                   project.AddFile(destFrameworkPath,
                                                   destFrameworkPath,
                                                   PBXSourceTree.Source));

            string moduleMapPath =
                Path.Combine(Path.Combine(destFrameworkFullPath, "Modules"),
                             "module.modulemap");

            if (File.Exists(moduleMapPath)) {
                // Parse the modulemap, format spec here:
                // http://clang.llvm.org/docs/Modules.html#module-map-language
                using (StreamReader moduleMapFile =
                       new StreamReader(moduleMapPath)) {
                    string line;
                    char[] delim = {' '};
                    while ((line = moduleMapFile.ReadLine()) != null) {
                        string[] items = line.TrimStart(delim).Split(delim, 2);
                        if (items.Length > 1) {
                            if (items[0] == "link") {
                                if (items[1].StartsWith("framework")) {
                                    items = items[1].Split(delim, 2);
                                    frameworks.Add(items[1].Trim(
                                        new char[] {'\"'}) + ".framework");
                                } else {
                                    linkFlags.Add("-l" + items[1]);
                                }
                            }
                        }
                    }
                }
            }

            string resourcesFolder = Path.Combine(destFrameworkFullPath,
                                                  "Resources");
            if (Directory.Exists(resourcesFolder)) {
                string[] resFiles = Directory.GetFiles(resourcesFolder);
                string[] resFolders =
                    Directory.GetDirectories(resourcesFolder);
                foreach (var resFile in resFiles) {
                    string destFile = Path.Combine("Resources",
                                                   Path.GetFileName(resFile));
                    File.Copy(resFile, Path.Combine(pathToBuiltProject,
                                                    destFile), true);
                    project.AddFileToBuild(
                        target, project.AddFile(destFile, destFile,
                                                PBXSourceTree.Source));
                }
                foreach (var resFolder in resFolders) {
                    string destFolder =
                        Path.Combine("Resources",
                                     new DirectoryInfo(resFolder).Name);
                    string destFolderFullPath =
                        Path.Combine(pathToBuiltProject, destFolder);
                    PlayServicesSupport.DeleteExistingFileOrDirectory(
                        destFolderFullPath);
                    Directory.Move(resFolder, destFolderFullPath);
                    project.AddFileToBuild(
                        target, project.AddFile(destFolderFullPath, destFolder,
                                                PBXSourceTree.Source));
                }
            }
        }

        foreach (var framework in frameworks) {
            project.AddFrameworkToProject(target, framework, false);
        }
        foreach (var linkFlag in linkFlags) {
            project.AddBuildProperty(target, "OTHER_LDFLAGS", linkFlag);
        }
        File.WriteAllText(pbxprojPath, project.WriteToString());
    }
}

}  // namespace Google

#endif  // UNITY_IOS
