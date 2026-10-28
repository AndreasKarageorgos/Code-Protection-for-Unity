#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace CodeProtection.Editor
{

    /// Runs after a player build and applies the passes that must happen on the
    /// finished build output:
    ///
    ///   1. Encrypt global-metadata.dat   (IL2CPP standalone and iOS)
    ///   2. Code Integrity Check          (standalone, any backend)
    ///
    /// Add Random Code and String Obfuscation no longer run here. They happen at
    /// compile time in the CodeProtection.CodeGen ILPostProcessor, which covers
    /// both Mono and IL2CPP. This processor still runs last for integrity, so the
    /// hashes it records match the final shipped bytes (including the compile time
    /// obfuscation and any metadata encryption).
    ///
    /// Metadata encryption alone makes the game fail to boot, because the IL2CPP
    /// runtime reads the raw file and rejects the scrambled header. It must be
    /// paired with the native decryptor in Native/metadata_decrypt_patch.cpp
    /// compiled into the IL2CPP runtime. See the README.

    public sealed class MetadataBuildPostProcessor : IPostprocessBuildWithReport
    {
        public int callbackOrder => 9000; // run late, after other post processors

        public void OnPostprocessBuild(BuildReport report)
        {
            var s = ProtectionSettingsEditor.GetOrCreate();
            if (s == null) return;

            var target = report.summary.platform;
            var scripting = PlayerSettings.GetScriptingBackend(
                BuildPipeline.GetBuildTargetGroup(target));
            string outputPath = report.summary.outputPath;

            try
            {
                if (s.encryptMetadata && scripting == ScriptingImplementation.IL2CPP)
                    RunMetadataEncryption(outputPath, target, s);

                // Integrity hashing must run LAST, after the step above has modified
                // the files, so the recorded hashes match what ships.
                if (s.codeIntegrityCheck && IsStandalone(target))
                    RunIntegrity(outputPath, target, s);
            }
            catch (Exception e)
            {
                Debug.LogError($"[CodeProtection] Post build failed: {e}");
            }
        }

        // ---- Encrypt global-metadata.dat (IL2CPP) -------------------------

        static void RunMetadataEncryption(string outputPath, BuildTarget target, ProtectionSettings s)
        {
            if (ProtectionSettingsEditor.IsPlaceholder(s.metadataPassphrase))
            {
                Debug.LogWarning("[CodeProtection] Metadata passphrase is still the placeholder. " +
                                 "Open Tools > Code Protection, set or generate a passphrase, and copy " +
                                 "the same value into Native/metadata_decrypt_patch.cpp. Encryption skipped.");
                return;
            }

            string metaPath = MetadataPath(outputPath, target);
            if (metaPath == null)
            {
                Debug.LogWarning($"[CodeProtection] Metadata encryption not wired for {target}. " +
                                 "Android ships the file inside the APK or AAB; see README.");
                return;
            }
            if (!File.Exists(metaPath))
            {
                Debug.LogWarning($"[CodeProtection] global-metadata.dat not found at {metaPath}.");
                return;
            }

            byte[] data = File.ReadAllBytes(metaPath);
            ulong seed = XorKeystream.SeedFromPassphrase(s.metadataPassphrase);
            XorKeystream.Apply(data, seed);
            File.WriteAllBytes(metaPath, data);

            Debug.Log($"[CodeProtection] Encrypted {metaPath} ({data.Length} bytes). " +
                      "Ensure the native decryptor is compiled in, or the build will not boot.");
        }

        static string MetadataPath(string outputPath, BuildTarget target)
        {
            const string tail = "il2cpp_data/Metadata/global-metadata.dat";
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneLinux64:
                {
                    string dir = Path.GetDirectoryName(outputPath);
                    string data = Path.GetFileNameWithoutExtension(outputPath) + "_Data";
                    return Path.Combine(dir, data, tail.Replace('/', Path.DirectorySeparatorChar));
                }
                case BuildTarget.StandaloneOSX:
                    return Path.Combine(outputPath, "Contents", "Resources", "Data",
                        tail.Replace('/', Path.DirectorySeparatorChar));
                case BuildTarget.iOS:
                    return Path.Combine(outputPath, "Data",
                        tail.Replace('/', Path.DirectorySeparatorChar));
                default:
                    return null; // Android handled separately (inside the archive)
            }
        }

        // ---- Code Integrity Check (standalone, any backend) ---------------

        static bool IsStandalone(BuildTarget t) =>
            t == BuildTarget.StandaloneWindows || t == BuildTarget.StandaloneWindows64 ||
            t == BuildTarget.StandaloneLinux64 || t == BuildTarget.StandaloneOSX;

        static string DataRoot(string outputPath, BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneLinux64:
                {
                    string dir = Path.GetDirectoryName(outputPath);
                    string data = Path.GetFileNameWithoutExtension(outputPath) + "_Data";
                    return Path.Combine(dir, data);
                }
                case BuildTarget.StandaloneOSX:
                    return Path.Combine(outputPath, "Contents", "Resources", "Data");
                default:
                    return null;
            }
        }

        static void RunIntegrity(string outputPath, BuildTarget target, ProtectionSettings s)
        {
            string root = DataRoot(outputPath, target);
            if (root == null || !Directory.Exists(root))
            {
                Debug.LogWarning("[CodeProtection] Data root not found; integrity check skipped.");
                return;
            }

            string secret = IntegritySecret.Value;
            if (ProtectionSettingsEditor.IsPlaceholder(secret))
            {
                Debug.LogWarning("[CodeProtection] Integrity secret is still the placeholder. " +
                                 "Open Tools > Code Protection and press Save to bake a strong secret, " +
                                 "let Unity recompile, then build again. Integrity manifest not written.");
                return;
            }
            if (secret != s.integrityPassphrase)
                Debug.LogWarning("[CodeProtection] Integrity passphrase in the panel differs from the " +
                                 "compiled secret. Press Save and let Unity recompile before building. " +
                                 "Signing with the compiled value for now.");

            string[] candidates =
            {
                "Managed/Assembly-CSharp.dll",
                "Managed/Assembly-CSharp-firstpass.dll",
                "il2cpp_data/Metadata/global-metadata.dat",
            };

            var entries = new System.Collections.Generic.List<IntegrityEntry>();
            foreach (var rel in candidates)
            {
                string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) continue;
                entries.Add(new IntegrityEntry { path = rel, hash = IntegrityManifest.Sha256File(full) });
            }

            if (entries.Count == 0)
            {
                Debug.LogWarning("[CodeProtection] No protected files found to hash; integrity manifest not written.");
                return;
            }

            var manifest = new IntegrityManifest
            {
                quitOnTamper = s.integrityQuitOnTamper,
                entries = entries.ToArray(),
            };
            manifest.Sign(secret);

            string cacheDir = Path.Combine(root, "StreamingAssets", "Cache");
            Directory.CreateDirectory(cacheDir);
            string manifestPath = Path.Combine(cacheDir, IntegritySecret.ManifestFileName);
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));

            Debug.Log($"[CodeProtection] Integrity manifest written ({entries.Count} file(s)) -> {manifestPath}");
        }
    }
}
#endif
