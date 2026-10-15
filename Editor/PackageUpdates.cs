using System;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Networking;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Nyades.BlenderRoundTrip
{
    // Requests run on the Editor update loop; opening the tool never blocks on the network.
    internal static class PackageUpdates
    {
        internal const string GitUrl = "https://github.com/N1ades/BlenderRoundTrip.git";
        const string ReleasesUrl = "https://github.com/N1ades/BlenderRoundTrip/releases/latest";
        const string ListingUrl = "https://raw.githubusercontent.com/N1ades/BlenderRoundTrip/vpm/index.json";
        const string ScriptGuid = "0f94d8d3bc6457a4793b123cb0e38371";
        const string Key = "Nyades.BlenderRoundTrip.Updates.";
        static UnityWebRequest request;
        static AddRequest install;
        static double nextCheck;
        static string error = "";
        static string latest = "";

        [Serializable] class Release { public string tag_name = ""; public bool draft = false; public bool prerelease = false; }
        [Serializable] class Manifest { public string version = ""; }

        static PackageInfo InstalledPackage => PackageInfo.FindForAssembly(typeof(BlenderRoundTrip).Assembly);

        internal static string ToolFile(string relative)
        {
            var package = InstalledPackage;
            if (package != null) return Path.Combine(package.resolvedPath, relative);
            string script = AssetDatabase.GUIDToAssetPath(ScriptGuid);
            if (string.IsNullOrEmpty(script)) throw new IOException("Cannot locate the Blender Round Trip installation.");
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(script), "..", relative));
        }

        static string CurrentVersion
        {
            get
            {
                var package = InstalledPackage;
                if (package != null) return package.version;
                try { return JsonUtility.FromJson<Manifest>(File.ReadAllText(ToolFile("package.json"))).version; }
                catch (Exception) { return "1.3.0"; }
            }
        }

        static bool IsNewer(string candidate, string installed) =>
            Version.TryParse(candidate, out var newer) && Version.TryParse(installed, out var current) && newer > current;

        internal static void Draw()
        {
            if (string.IsNullOrEmpty(latest)) latest = EditorPrefs.GetString(Key + "Latest", "");
            if (EditorApplication.timeSinceStartup >= nextCheck && request == null && install == null)
            {
                nextCheck = EditorApplication.timeSinceStartup + 3600;
                if (!long.TryParse(EditorPrefs.GetString(Key + "Checked", "0"), out var checkedAt) ||
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() - checkedAt >= 3600) Check();
            }
            EditorGUILayout.Space();
            if (IsNewer(latest, CurrentVersion))
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField("Update available: " + CurrentVersion + " → " + latest, EditorStyles.wordWrappedMiniLabel);
                    var package = InstalledPackage;
                    using (new EditorGUI.DisabledScope(install != null))
                    {
                        if (package != null && package.source == PackageSource.Git)
                        {
                            if (GUILayout.Button(install != null ? "Updating…" : "Update via UPM", GUILayout.Width(120)))
                            {
                                try
                                {
                                    install = Client.Add(GitUrl + "#v" + latest);
                                    EditorApplication.update -= Poll;
                                    EditorApplication.update += Poll;
                                }
                                catch (Exception e) { error = e.Message; }
                            }
                        }
                        else if (package != null && package.source == PackageSource.Embedded)
                        {
                            if (GUILayout.Button("Open VCC", GUILayout.Width(100)))
                                Application.OpenURL("vcc://vpm/addRepo?url=" + Uri.EscapeDataString(ListingUrl));
                        }
                        else if (GUILayout.Button("Download update", GUILayout.Width(120))) Application.OpenURL(ReleasesUrl);
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Blender Round Trip " + CurrentVersion, EditorStyles.miniLabel);
                using (new EditorGUI.DisabledScope(request != null || install != null))
                    if (GUILayout.Button(request != null ? "Checking…" : "Check for updates", EditorStyles.miniButton, GUILayout.Width(120))) Check();
            }
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.LabelField(error, EditorStyles.wordWrappedMiniLabel);
        }

        static void Check()
        {
            if (request != null) return;
            error = "";
            request = UnityWebRequest.Get("https://api.github.com/repos/N1ades/BlenderRoundTrip/releases/latest");
            request.SetRequestHeader("Accept", "application/vnd.github+json");
            request.SetRequestHeader("User-Agent", "Nyades-BlenderRoundTrip");
            request.timeout = 10;
            request.SendWebRequest();
            EditorPrefs.SetString(Key + "Checked", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
            nextCheck = EditorApplication.timeSinceStartup + 3600;
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
            AssemblyReloadEvents.beforeAssemblyReload -= Cleanup;
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
        }

        static void Poll()
        {
            if (request != null && request.isDone)
            {
                try
                {
                    if (request.result != UnityWebRequest.Result.Success) error = "Update check unavailable. Use Check for updates to retry.";
                    else
                    {
                        var release = JsonUtility.FromJson<Release>(request.downloadHandler.text);
                        string version = release.tag_name == null ? "" : release.tag_name.TrimStart('v');
                        if (!release.draft && !release.prerelease && Version.TryParse(version, out _))
                        {
                            latest = version;
                            EditorPrefs.SetString(Key + "Latest", latest);
                            if (!IsNewer(latest, CurrentVersion)) error = "You have the latest release.";
                        }
                    }
                }
                catch (Exception) { error = "Could not read release information."; }
                finally { request.Dispose(); request = null; }
                Repaint();
            }
            if (install != null && install.IsCompleted)
            {
                if (install.Status == StatusCode.Failure) error = "Update failed: " + install.Error.message;
                install = null;
                Repaint();
            }
            if (request == null && install == null) EditorApplication.update -= Poll;
        }

        static void Repaint()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<BlenderRoundTrip>()) window.Repaint();
            foreach (var window in Resources.FindObjectsOfTypeAll<FTLivePreview>()) window.Repaint();
        }

        static void Cleanup()
        {
            if (request != null) { request.Abort(); request.Dispose(); request = null; }
            EditorApplication.update -= Poll;
        }
    }
}
