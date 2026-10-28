using System.IO;
using System.Threading;
using UnityEngine;

namespace CodeProtection
{
    /// Runs automatically at game startup (before the first scene). Reads the
    /// signed manifest from StreamingAssets/Cache, re hashes the listed files, and
    /// if the signature is wrong or any file was changed, closes the app.
    ///
    /// Does nothing in the editor and does nothing if no manifest is present (the
    /// feature was off at build time), so it is safe to leave in. Standalone only:
    /// StreamingAssets is a plain file path there. On Android it lives inside the
    /// APK and needs UnityWebRequest, which is not handled here.
    public static class CodeIntegrityVerifier
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Verify()
        {
            if (Application.isEditor) return;

            string manifestPath = Path.Combine(Application.streamingAssetsPath, "Cache",
                                                IntegritySecret.ManifestFileName);
            if (!File.Exists(manifestPath)) return; // feature not enabled for this build

            IntegrityManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<IntegrityManifest>(File.ReadAllText(manifestPath));
            }
            catch
            {
                // A corrupt or replaced manifest is itself a tamper signal.
                Fail("integrity manifest unreadable", true);
                return;
            }
            if (manifest == null) { Fail("integrity manifest empty", true); return; }

            // 1. The manifest must be signed with our secret.
            if (!manifest.VerifySignature(IntegritySecret.Value))
            {
                Fail("integrity manifest signature mismatch", manifest.quitOnTamper);
                return;
            }

            // 2. Every listed file must still match its recorded hash.
            string root = Application.dataPath;
            foreach (var entry in manifest.entries)
            {
                string full = Path.Combine(root, entry.path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full))
                {
                    Fail("protected file missing: " + entry.path, manifest.quitOnTamper);
                    return;
                }

                string actual = TryHash(full);
                if (actual == null)
                {
                    // A protected file we cannot read is treated as tamper. Holding
                    // a file open to dodge the check is a known bypass, so fail
                    // closed here rather than skipping it. We retry once first so a
                    // brief antivirus or indexer lock does not punish a real player.
                    Fail("protected file unreadable: " + entry.path, manifest.quitOnTamper);
                    return;
                }

                if (actual != entry.hash)
                {
                    Fail("protected file modified: " + entry.path, manifest.quitOnTamper);
                    return;
                }
            }
        }

        static string TryHash(string full)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { return IntegrityManifest.Sha256File(full); }
                catch
                {
                    if (attempt == 0) Thread.Sleep(150); // one short retry for a transient lock
                }
            }
            return null;
        }

        static void Fail(string reason, bool quit)
        {
            Debug.LogError("[CodeProtection] Integrity check failed: " + reason);
            if (!quit) return;
            Application.Quit(1);
            // Application.Quit is deferred to end of frame; make sure nothing
            // meaningful runs in the meantime.
            if (Application.isPlaying) throw new System.Exception("integrity check failed");
        }
    }
}
