using System.Collections.Generic;
using UnityEngine;

namespace CodeProtection
{

    /// Single source of truth for the panel toggles. Stored as an asset so the
    /// choices survive domain reloads and travel with the project.
    ///
    /// This type is pure runtime: it holds no editor calls, so it lives in the
    /// runtime assembly without pulling UnityEditor into a player build. The asset
    /// is loaded and created by ProtectionSettingsEditor (Editor assembly). It is
    /// never placed in Resources and nothing references it at runtime, so it does
    /// not ship inside the game.

    public sealed class ProtectionSettings : ScriptableObject
    {
        public const string PlaceholderMetadata = "GENERATE-ME";
        public const string PlaceholderIntegrity = "GENERATE-ME";

        [Header("Encrypt global-metadata.dat (IL2CPP only)")]
        public bool encryptMetadata = false;
        [Tooltip("Passphrase that derives the keystream. Must match the value " +
                 "compiled into the native decryptor. Keep it out of source control.")]
        public string metadataPassphrase = PlaceholderMetadata;

        [Header("Add Random Code")]
        public bool addRandomCode = false;
        [Tooltip("When ON, random code is injected even into classes without the " +
                 "[Protect] attribute. When OFF, only [Protect] classes are touched.")]
        public bool forceAll = false;
        [Tooltip("How many junk methods to add per processed type.")]
        [Range(0, 20)] public int junkMethodsPerType = 4;
        [Tooltip("Inject a dead branch call into real methods so the junk is " +
                 "referenced (harder for decompilers to prune). Never executes.")]
        public bool injectDeadCalls = true;
        [Tooltip("Namespaces to always skip (prefix match), comma separated.")]
        public string excludeNamespaces = "Unity,UnityEngine,UnityEditor,System,TMPro";

        [Header("String Obfuscation")]
        public bool obfuscateStrings = false;
        [Tooltip("Only obfuscate string literals at least this many characters long. " +
                 "Very short strings add noise for little gain.")]
        [Range(1, 32)] public int minStringLength = 2;

        [Header("Code Integrity Check (Standalone only)")]
        public bool codeIntegrityCheck = false;
        [Tooltip("Secret used to sign the integrity manifest. Pressing Save writes " +
                 "this into IntegritySecret.generated.cs so build and runtime match. " +
                 "Leave it on the placeholder and Save generates a strong one for you.")]
        public string integrityPassphrase = PlaceholderIntegrity;
        [Tooltip("Close the app when a protected file has been modified. If off, the " +
                 "tamper is only logged (useful while testing).")]
        public bool integrityQuitOnTamper = true;

        // ---- Per project randomized markers (hardening) ----
        // Generated once so the obfuscation does not use fixed, greppable names.
        // Not meant to be edited by hand. Press "Re-randomize markers" to rotate.
        [HideInInspector] public string decoderType = "";
        [HideInInspector] public string decoderMethod = "";
        [HideInInspector] public string fieldPrefix = "";
        [HideInInspector] public string methodPrefix = "";
        [HideInInspector] public int markerSalt = 0;
        [HideInInspector] public string manifestFile = "integrity.manifest.json";


        /// Set by ProtectionSettingsEditor once the asset is loaded in the editor.
        /// Runtime code never needs the settings object, so this stays null in a
        /// player build.
  
        public static ProtectionSettings Instance { get; set; }

        public List<string> ExcludedNamespaceList()
        {
            var list = new List<string>();
            foreach (var part in (excludeNamespaces ?? "").Split(','))
            {
                var t = part.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }
    }
}
