#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace CodeProtection.Editor
{

    /// Control panel for the asset. Collapsible sections, each with an ON or OFF
    /// toggle and an info blurb. Window menu: Tools > Code Protection.
  
    public sealed class ProtectionPanel : EditorWindow
    {
        Vector2 _scroll;
        bool _secMeta = true, _secRandom = true, _secStrings = true, _secIntegrity = true;

        static readonly Color Header   = new Color(0.60f, 0.52f, 0.12f);
        static readonly Color Info     = new Color(0.30f, 0.30f, 0.33f);
        static readonly Color OnGreen  = new Color(0.35f, 0.70f, 0.40f);

        [MenuItem("Tools/Code Protection")]
        static void Open() => GetWindow<ProtectionPanel>("Code Protection").minSize = new Vector2(520, 440);

        void OnGUI()
        {
            var s = ProtectionSettingsEditor.GetOrCreate();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.Space(6);

            // --- Encrypt global-metadata.dat ---
            _secMeta = Section("Encrypt global-metadata.dat (Beta)",
                "Encrypt the IL2CPP global-metadata.dat and decrypt it at runtime.",
                _secMeta, ref s.encryptMetadata);
            if (_secMeta)
            {
                Blurb("Only affects IL2CPP builds (Standalone, iOS). The file holds the structure " +
                      "of your game's metadata; scrambling it blocks casual rippers like Il2CppDumper. " +
                      "Requires the native decryptor compiled into the IL2CPP runtime. See the README.");
                using (new EditorGUI.DisabledScope(!s.encryptMetadata))
                {
                    s.metadataPassphrase = SecretField("Passphrase", s.metadataPassphrase);
                    Blurb("This passphrase must match CP_METADATA_PASSPHRASE in " +
                          "Native/metadata_decrypt_patch.cpp. Keep it out of public source control.");
                }
                EditorGUILayout.Space(4);
            }

            // --- Add Random Code ---
            _secRandom = Section("Add Random Code",
                "Activate to add random code to your classes.",
                _secRandom, ref s.addRandomCode);
            if (_secRandom)
            {
                Blurb("Generates junk methods from random arithmetic and injects them into included " +
                      "classes. Everything injected sits behind an always false branch, so runtime " +
                      "behaviour is unchanged. Runs at compile time, so it applies to both Mono and " +
                      "IL2CPP builds. It only runs for player builds, never in the editor.");
                using (new EditorGUI.DisabledScope(!s.addRandomCode))
                {
                    Blurb("Force: inject even into classes without the [Protect] attribute. " +
                          "When OFF, only [Protect] classes are touched.");
                    s.forceAll = Toggle("Force adding random code", s.forceAll);
                    s.junkMethodsPerType = EditorGUILayout.IntSlider("Junk methods / type", s.junkMethodsPerType, 0, 20);
                    s.injectDeadCalls = Toggle("Inject dead branch calls", s.injectDeadCalls);
                    s.excludeNamespaces = Field("Exclude namespaces", s.excludeNamespaces);
                }
                EditorGUILayout.Space(4);
            }

            // --- String Obfuscation ---
            _secStrings = Section("String Obfuscation",
                "Encrypt string literals so decompilers do not show them.",
                _secStrings, ref s.obfuscateStrings);
            if (_secStrings)
            {
                Blurb("Replaces every string literal in your code with an encrypted form plus a tiny " +
                      "decoder emitted into the same assembly. The strings decode at runtime to the " +
                      "exact same values, so behaviour is unchanged, but dnSpy and ILSpy no longer show " +
                      "the plain text. Uses the same class selection as Add Random Code ([Protect] or " +
                      "Force). Runs at compile time, so it applies to both Mono and IL2CPP builds.");
                using (new EditorGUI.DisabledScope(!s.obfuscateStrings))
                {
                    s.minStringLength = EditorGUILayout.IntSlider("Min string length", s.minStringLength, 1, 32);
                    Blurb("Put [DoNotProtect] on code that compares strings by reference or relies on " +
                          "interning. Value comparisons and reflection by name are unaffected.");
                }
                EditorGUILayout.Space(4);
            }

            // --- Code Integrity Check ---
            _secIntegrity = Section("Code Integrity Check",
                "Adds checks that verify your game's files at startup.",
                _secIntegrity, ref s.codeIntegrityCheck);
            if (_secIntegrity)
            {
                Blurb("Hashes your critical files at build time into a signed manifest placed in " +
                      "StreamingAssets/Cache (ships with the game). At startup the runtime re hashes " +
                      "them and closes the app if anything was modified. Standalone (Windows, Linux, " +
                      "macOS) builds only.");
                using (new EditorGUI.DisabledScope(!s.codeIntegrityCheck))
                {
                    s.integrityPassphrase = SecretField("Integrity passphrase", s.integrityPassphrase);
                    Blurb("Signs the manifest. Press Save to bake it into the game (rewrites " +
                          "IntegritySecret.generated.cs); let Unity recompile before building. A strong " +
                          "value was generated for you the first time; you rarely need to change it.");
                    s.integrityQuitOnTamper = Toggle("Quit on tamper", s.integrityQuitOnTamper);
                }
                EditorGUILayout.Space(4);
            }

            EditorGUILayout.Space(8);
            if (GUILayout.Button("Save", GUILayout.Height(26)))
            {
                if (ProtectionSettingsEditor.IsPlaceholder(s.integrityPassphrase))
                    s.integrityPassphrase = ProtectionSettingsEditor.GenerateSecret();

                ProtectionSettingsEditor.EnsureMarkers(s);
                EditorUtility.SetDirty(s);
                AssetDatabase.SaveAssets();
                ProtectionSettingsEditor.WriteIntegritySecret(s);
                ProtectionSettingsEditor.WriteConfig(s);
            }

            if (GUILayout.Button("Rotate markers", GUILayout.Height(20)))
            {
                ProtectionSettingsEditor.EnsureMarkers(s, force: true);
                EditorUtility.SetDirty(s);
                AssetDatabase.SaveAssets();
                ProtectionSettingsEditor.WriteIntegritySecret(s);
                ProtectionSettingsEditor.WriteConfig(s);
                Debug.Log("[CodeProtection] Rotated the obfuscation marker names and salt.");
            }

            EditorGUILayout.HelpBox(
                "Press Save after any change so the compile time config is rewritten.\n" +
                "Add Random Code and String Obfuscation run during compilation, for both Mono and IL2CPP.\n" +
                "Metadata encryption needs the native decryptor compiled in first.",
                MessageType.Info);

            EditorGUILayout.EndScrollView();

            if (GUI.changed) EditorUtility.SetDirty(s);
        }

        // ---- styled widgets ----

        bool Section(string title, string subtitle, bool expanded, ref bool toggle)
        {
            var r = EditorGUILayout.GetControlRect(false, 40);
            EditorGUI.DrawRect(r, Header);

            var foldR = new Rect(r.x + 6, r.y + 2, 16, 16);
            expanded = EditorGUI.Foldout(foldR, expanded, GUIContent.none, true);

            var titleStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = Color.white } };
            EditorGUI.LabelField(new Rect(r.x + 24, r.y + 2, r.width - 110, 18), title, titleStyle);

            var subStyle = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(1, 1, 1, 0.75f) } };
            EditorGUI.LabelField(new Rect(r.x + 24, r.y + 20, r.width - 110, 16), subtitle, subStyle);

            var pillR = new Rect(r.xMax - 70, r.y + 10, 56, 20);
            DrawToggle(pillR, ref toggle);
            return expanded;
        }

        void DrawToggle(Rect r, ref bool value)
        {
            EditorGUI.DrawRect(r, value ? OnGreen : new Color(0.35f, 0.35f, 0.38f));
            var style = new GUIStyle(EditorStyles.miniBoldLabel)
            { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white } };
            if (GUI.Button(r, value ? "ON" : "OFF", style))
                value = !value;
        }

        void Blurb(string text)
        {
            var content = new GUIContent(text);
            var style = new GUIStyle(EditorStyles.wordWrappedMiniLabel)
            { normal = { textColor = new Color(1, 1, 1, 0.65f) }, padding = new RectOffset(8, 8, 6, 6) };
            float h = style.CalcHeight(content, position.width - 24);
            var box = GUILayoutUtility.GetRect(position.width - 16, h);
            box.x += 8; box.width -= 16;
            EditorGUI.DrawRect(box, Info);
            EditorGUI.LabelField(box, content, style);
        }

        string Field(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(150));
            value = EditorGUILayout.TextField(value);
            EditorGUILayout.EndHorizontal();
            return value;
        }

        string SecretField(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(150));
            value = EditorGUILayout.TextField(value);
            if (GUILayout.Button("Generate", GUILayout.Width(80)))
            {
                value = ProtectionSettingsEditor.GenerateSecret();
                GUI.FocusControl(null);
            }
            EditorGUILayout.EndHorizontal();
            return value;
        }

        bool Toggle(string label, bool value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(220));
            var r = GUILayoutUtility.GetRect(56, 20, GUILayout.Width(56));
            DrawToggle(r, ref value);
            EditorGUILayout.EndHorizontal();
            return value;
        }
    }
}
#endif
