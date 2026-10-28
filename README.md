# Code Protection for Unity

A small, open toolkit that reproduces four features from paid Unity obfuscators, so you own the code and can extend it:

1. **String Obfuscation** encrypts the string literals in your compiled code so decompilers no longer show them in plain text.
2. **Add Random Code** injects behaviour neutral junk code into your classes to slow down decompilers.
3. **Code Integrity Check** hashes critical files at build time and closes the app if they are modified.
4. **Encrypt global-metadata.dat** scrambles the IL2CPP metadata file on disk and decrypts it at runtime.

This is a starting point you own and can extend, not a turnkey product. Please read the threat model and the honest limitations near the bottom before you rely on it.

## Requirements

* Unity 6 (6000.x). Older versions may work but the compile time passes were set up and tested against Unity 6.
* The Mono.Cecil package, for String Obfuscation and Add Random Code. Install it once: open **Window > Package Manager**, click the **+** button, choose **Install package by name**, enter `com.unity.nuget.mono-cecil`, and install. If you only want the Code Integrity Check and metadata encryption, you can skip this package.

## Install

1. If you want String Obfuscation or Add Random Code, install `com.unity.nuget.mono-cecil` as described above.
2. Copy the `CodeProtection` folder into your project's `Assets/` folder. Any location inside `Assets/` works; the tool finds itself.
3. In Unity, open the panel from the top menu: **Tools > Code Protection**.
4. Turn on the features you want and press **Save**. Let Unity finish recompiling.
5. Build your game as you normally would. The enabled passes run automatically.

A few things are handled for you:

* **Works on both Mono and IL2CPP.** String Obfuscation and Add Random Code run at compile time through a Unity ILPostProcessor (the `Unity.CodeProtection.CodeGen` assembly), before IL2CPP converts your code to C++. There is no separate route to wire up for IL2CPP.
* **Secrets are generated for you.** The first time the panel opens, a strong random passphrase is created for the integrity check and for metadata encryption. You never ship a guessable default.
* **The settings asset is created automatically** and is never included in your build, so your passphrases do not travel inside the game through it.
* **The tool finds its own folder**, so you can drop `CodeProtection` anywhere under `Assets/`.

> **Before anything else:** put your project under version control (git) and test on a throwaway build first. Any build tooling can have edge cases, and version control makes every change a one click revert.

## What each feature does, and how to use it

### 1. String Obfuscation

**What it does.** At compile time, each string literal in your code is replaced with an encrypted form, together with a tiny decoder emitted into the same assembly. At runtime the decoder returns the exact same text, so your game behaves identically, but a decompiler such as dnSpy or ILSpy shows scrambled characters instead of readable strings. This hides things like URLs, keys, product identifiers, error messages, and labels that would otherwise give a reader a free map of your code.

**Step by step.**

1. Open **Tools > Code Protection** and turn **String Obfuscation** on.
2. Choose what gets processed:
   * Tag specific classes with `[Protect]`, or
   * Turn on **Force adding random code** to process everything except excluded namespaces.
3. Set **Min string length** if you want to leave very short strings alone. Two is a good default.
4. Press **Save**, then build. It applies to both Mono and IL2CPP.

**Caveat.** Put `[CodeProtection.DoNotProtect]` on code that compares strings by reference or depends on string interning. Ordinary value comparisons (`==`, `Equals`, dictionary keys) and reflection by name keep working, because the decoded value is identical.

### 2. Add Random Code

**What it does.** At compile time, Mono.Cecil rewrites your code to add random junk methods to each processed class, and optionally inserts a call to one of them at the top of your real methods. Every injected call sits behind a flag that is always false, so **nothing extra ever runs**. The only effect is more methods and more noise for anyone who opens your code in a decompiler.

**Step by step.**

1. Open **Tools > Code Protection** and turn **Add Random Code** on.
2. Decide what gets processed, in one of two ways:
   * **Tag specific classes.** Add the attribute above each class you care about:
     ```csharp
     using CodeProtection;

     [Protect]
     public class LicenseValidator : MonoBehaviour
     {
         // your code
     }
     ```
   * **Or process everything.** Turn on **Force adding random code** in the panel. Then every class is processed except the excluded namespaces and anything you explicitly opt out.
3. Protect anything fragile from being touched with the opposite attribute. This wins even when Force is on:
   ```csharp
   [CodeProtection.DoNotProtect]
   public class SaveSerializer { }
   ```
   Use it on timing sensitive code, serialization callbacks, and anything reached through reflection.
4. Tune the panel options if you wish:
   * **Junk methods / type** sets how many junk methods are added per class.
   * **Inject dead branch calls** inserts the always false call into your real methods. Turn it off if you only want extra methods and no changes inside existing ones.
   * **Exclude namespaces** is a comma separated prefix list that is always skipped.
5. Press **Save**, then build.

**Which classes should I tag?** The gameplay logic worth hiding: licensing checks, anti cheat, scoring, in app purchase validation, core rules. You do not need to tag everything.

**How it runs.** Both this and String Obfuscation run inside the `Unity.CodeProtection.CodeGen` ILPostProcessor. It only touches player builds (it is skipped in the editor and in play mode), it only touches `Assembly-CSharp` and `Assembly-CSharp-firstpass`, and on any error it leaves your assembly untouched rather than shipping broken code.

### 3. Code Integrity Check

**What it does.** Two halves, tied together by one secret passphrase.

* **At build time (automatic):** after a standalone build, the post build step computes a SHA256 hash of each critical file, writes them into a manifest, signs the manifest with HMAC SHA256, and drops it inside `StreamingAssets/Cache` so it ships with the game. This runs last, after the compile time obfuscation and the metadata pass, so the hashes match the final shipped bytes.
* **At runtime (automatic):** on startup the verifier re hashes the listed files. If the manifest signature is wrong, any file was changed, or a protected file cannot be read, it closes the app.

**Step by step.**

1. Open **Tools > Code Protection** and turn **Code Integrity Check** on. A strong passphrase is already filled in; keep it or press **Generate**.
2. Set **Quit on tamper**:
   * **On** for your real release: the game closes when a protected file is modified.
   * **Off** while testing: it only writes a log line instead of closing.
3. Press **Save**. This bakes the passphrase into `IntegritySecret.generated.cs`, so build time signing and runtime checking use the same secret.
4. **Wait for Unity to finish recompiling** before you build. If you build during compilation the game may carry an old secret.
5. Build. Watch the Console for a line like `Integrity manifest written (N file(s))`.

**What gets checked.** By default: `Assembly-CSharp.dll`, `Assembly-CSharp-firstpass.dll` (Mono), and `global-metadata.dat` (IL2CPP), whichever exist. To protect more files, edit the `candidates` array in `MetadataBuildPostProcessor.RunIntegrity`.

**Hardening note.** A protected file that cannot be read at startup is treated as tamper (after one short retry), because holding a file open was a simple way to skip the check. The retry means a brief antivirus or indexer lock does not punish a real player.

**Scope.** Standalone only, because `StreamingAssets` is a plain file path there. On Android the manifest lives inside the APK and needs `UnityWebRequest` to read, plus archive repacking to write, which is not handled here. The verifier is safe to leave in the project even when the feature is off: with no manifest present it does nothing.

### 4. Encrypt global-metadata.dat (advanced, needs manual native work)

> **Read this before turning it on.** This is the only feature with a manual, version specific step. If you enable the toggle but skip the native patch, your IL2CPP build will crash on startup, because the file gets scrambled but nothing decrypts it. If your game crashed right after a build, this toggle being on is the usual cause: turn it off, rebuild, and it will boot again.

**What it does.** Two halves that must agree on one passphrase.

* **Encrypt (managed, automatic):** the post build step finds `global-metadata.dat` in the finished build and applies a keystream derived from your passphrase.
* **Decrypt (native, manual):** the IL2CPP runtime loads that file through a function inside Unity's own `libil2cpp` C++ source, before any of your code runs. You must patch that function to reverse the keystream in memory. The patch and the matching keystream are in `Native/metadata_decrypt_patch.cpp`.

**Step by step.**

1. Confirm your build is **IL2CPP** (Player Settings > Other Settings > Scripting Backend). On Mono this feature does nothing.
2. Open `Native/metadata_decrypt_patch.cpp` and set `CP_METADATA_PASSPHRASE` to the same value shown in the panel's metadata passphrase field.
3. Patch your Unity version's libil2cpp, at:
   `…/Unity/Hub/Editor/<version>/Editor/Data/il2cpp/libil2cpp/vm/MetadataLoader.cpp`
   Follow the commented drop in body inside `metadata_decrypt_patch.cpp`. The recommended way is to do this behind a build hook that backs the file up before the build and restores it after, so you never permanently alter your editor install. An `IPreprocessBuildWithReport` that copies the original aside and writes the patched version, paired with an `IPostprocessBuildWithReport` that restores it, does this cleanly.
4. In the panel, turn on **Encrypt global-metadata.dat**, make sure the passphrase matches step 2, and press **Save**.
5. Build, then test. If it boots, the patch and passphrase line up. If it crashes on startup, the passphrase or the function patch is off.

**Why the native half is manual.** `global-metadata.dat` is not loaded by your code. It is loaded by `il2cpp::vm::MetadataLoader::LoadMetadataFile` inside libil2cpp, before your code runs, so the only reliable place to decrypt is inside that function. Unity copies libil2cpp from the editor into each build, which is why patching the editor's copy (behind a backup and restore hook) is the practical route.

**Version sensitivity.** The signature and body of `LoadMetadataFile` (and the matching free function) change between Unity versions. Open your version's file, find the function, and adapt the snippet. Do not paste it blindly. This is the genuinely fragile part of any metadata encryption feature, paid ones included; they ship per version patches for exactly this reason.

**Platforms.** Standalone (Windows, Linux, macOS) and iOS paths are wired in `MetadataBuildPostProcessor`. **Android** ships the file compressed inside the APK or AAB, so encrypting it means unzipping, replacing, and repacking the archive as a later step, which is not included here.

## The two passphrases

* **Metadata passphrase** belongs to the metadata encryption feature. It must match `CP_METADATA_PASSPHRASE` in `Native/metadata_decrypt_patch.cpp`.
* **Integrity passphrase** belongs to the Code Integrity Check. Pressing **Save** keeps it in sync automatically by baking it into `IntegritySecret.generated.cs`.

Both are generated strong on first use. The integrity secret is stored masked, so a plain `strings` scan of the shipped game does not reveal it. Keep both values out of public source control.

## Recommended setup by backend

* **Mono standalone:** use **String Obfuscation**, **Add Random Code**, and **Code Integrity Check**. The metadata feature does not apply (it is IL2CPP only), so leave it off.
* **IL2CPP standalone:** **String Obfuscation**, **Add Random Code**, and **Code Integrity Check** all work out of the box. **Encrypt global-metadata.dat** additionally needs the native patch. Start with the first three, then add metadata encryption when you are ready for the native step.

## Security review summary

This asset is honest about being obfuscation rather than security, and the following hardening was applied so the defaults are not themselves the weak point:

* **No guessable defaults.** The old placeholder passphrases are gone. Strong random secrets are generated on first use.
* **The integrity secret is stored masked**, not as a plain string constant, so it does not fall out of a simple `strings` scan of the build.
* **The integrity verifier fails closed.** A protected file that cannot be read is treated as tamper after one short retry, closing the trick of locking a file to skip the check.
* **A stronger keystream.** Metadata encryption uses a 64 bit seed with an xorshift64* keystream on both the C# and C++ sides, replacing the weaker 32 bit version. It is still a stream cipher whose key ships in the game, so swap in AES or ChaCha20 on both sides if you need real strength.
* **Randomized markers.** The string decoder's type and method names, the junk field and method prefixes, a per project salt mixed into the string cipher, and the integrity manifest filename are all randomized per project. This removes the fixed, greppable names that would let anyone point a one click un protector at every game built with this tool. Press **Rotate markers** in the panel to roll them again.
* **The settings asset never ships**, so build time passphrases do not travel inside the game through it.

## Threat model (please read)

Everything here runs on the player's machine, so treat it as raising effort, not as a wall. Here is the honest picture for each feature, including what changes now that this repository is public.

* **String Obfuscation.** Stops a casual decompile from showing your strings. It does not survive someone who runs or traces the code, because the decoder and the per string key both ship with the game. Public source lowers the effort to write an automated un protector, though generic tools already deobfuscate closed source string encryptors by invoking the decoder dynamically. The randomized decoder name and per project salt mean a single static pattern does not match every game, which raises the cost of a one size fits all tool.

* **Add Random Code.** Stops nothing on its own; it slows a human reading the code. Automated dead code and unused method cleaners remove most of it. Randomized prefixes stop a trivial name based cleaner, but a control flow aware tool still prunes the always false branches.

* **Code Integrity Check.** Stops casual file edits and simple mods, which is the common cheat vector. It does not survive an attacker who extracts the baked secret, edits a file, recomputes the hash, and re signs the manifest. Public source makes writing that script easy; the real limit is architectural, because the secret must live in the build. Randomizing the manifest filename and masking the secret remove the easy tells.

* **Encrypt global-metadata.dat.** Stops on disk rippers like Il2CppDumper that read the raw file. The passphrase is not in your managed code or in the .dat file; it lives in the compiled native patch, so reading this repository does not hand anyone the key. It does not survive a memory dump, because the file is decrypted in memory before use, and it does not survive someone reverse engineering the native binary to recover the key.

**The bottom line.** Making this repository public removes the step of figuring out the scheme, which for client side obfuscation was never the real barrier. Keeping it private would buy almost nothing (security through obscurity). Anything that truly must not be cheated or pirated belongs on a server you control. Use this to stop casual ripping and raise the effort, and market it that way.

## Troubleshooting

* **Game crashes or dumps on startup right after building.** The metadata encryption toggle is almost certainly on without the native patch. Turn it off, rebuild, confirm it boots, then enable features one at a time.
* **`The type or namespace name 'Cecil' does not exist in 'Mono'`.** The Mono.Cecil package is not installed, or the compile time assembly is set to Any Platform. Install `com.unity.nuget.mono-cecil`, and make sure the `Unity.CodeProtection.CodeGen` assembly is set to **Editor** only, not Any Platform. See `Editor/Mono.Cecil_fallback.txt` for the full checklist.
* **`ILPostProcessor` or `ICompiledAssembly` could not be found.** The compile time assembly name must start with `Unity.` and end with `.CodeGen` for Unity to supply the pipeline types. Keep the name field as `Unity.CodeProtection.CodeGen`. See `Editor/Mono.Cecil_fallback.txt`.
* **You want to drop the compile time passes entirely.** Delete the `CodeProtection/ILPostProcessor` folder. Add Random Code and String Obfuscation stop; the Code Integrity Check and metadata encryption keep working.
* **`'IntegritySecret' is inaccessible due to its protection level`.** The generated class must be `public`. The shipped version already is; if you edited it, change it back.
* **A pass did nothing.** Either no class has `[Protect]` and Force is off, or you did not press **Save** after turning the feature on (Save rewrites the compile time config), or you are looking at an editor or play mode run (the passes only run for player builds).
* **Integrity check never triggers.** Confirm the Console printed `Integrity manifest written` at build time, and that the manifest exists under `StreamingAssets/Cache` in the build. In the editor the verifier is intentionally inactive.
* **Where is the crash log on Windows?** `%USERPROFILE%\AppData\LocalLow\<CompanyName>\<ProductName>\Player.log`. A mention of `global-metadata` or `MetadataLoader` confirms the metadata feature as the cause.

## Honest limitations

* **This is obfuscation, not security.** The decryption key, the integrity secret, and all injected code ship inside the game and run on the player's machine. A determined attacker with the right tools will get through. The realistic goal is to stop casual ripping and raise the effort, not to make the game uncrackable. No client side scheme, free or paid, achieves the latter.
* The metadata keystream is a stream cipher. It is stronger than before but still not real cryptography. Swap it for AES or ChaCha20 on both the C# and C++ sides if you need more, keeping the two byte for byte identical.
* The integrity secret ships inside the game, so an attacker who extracts it can pass the check. It reliably stops casual file edits, not a determined reverse engineer.
* String obfuscation hides literals from a casual decompile. The decoder ships with the game, so a reader who runs or traces the code can still recover the strings.
* Always keep anti cheat and anti piracy logic that truly matters on a server you control.
* Test on a throwaway build first, and keep backups.

## File map

```
CodeProtection/
  CodeProtection.asmdef             runtime assembly definition
  LICENSE
  CodeGen/
    XorKeystream.cs                 keystream (mirror of the C++ side)
    ProtectionAttributes.cs         [Protect] / [DoNotProtect]
    ProtectionSettings.cs           settings data (pure runtime)
    IntegrityManifest.cs            manifest model + SHA256/HMAC helpers
    IntegritySecret.generated.cs    baked signing secret + manifest name, masked
    CodeIntegrityVerifier.cs        runtime startup check (quits on tamper)
  Editor/
    CodeProtection.Editor.asmdef    editor assembly (no Cecil)
    ProtectionPanel.cs              Tools > Code Protection window
    ProtectionSettingsEditor.cs     asset create/load, secret + marker generation, config writer
    MetadataBuildPostProcessor.cs   post build: metadata encrypt + integrity
    RandomCodeInjector.cs           empty; logic moved to the ILPostProcessor
    Mono.Cecil_fallback.txt         notes on the compile time assembly setup
  ILPostProcessor/
    CodeProtection.CodeGen.asmdef   compile time assembly (name field: Unity.CodeProtection.CodeGen, Editor only, references Mono.Cecil)
    ProtectionILPostProcessor.cs    random code + string obfuscation (Mono and IL2CPP)
  Native/
    metadata_decrypt_patch.cpp      libil2cpp decrypt patch + keystream
```
