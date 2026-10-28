// The random code and string obfuscation passes moved to a compile time
// ILPostProcessor so they work on both Mono and IL2CPP builds out of the box.
//
// See: CodeProtection/ILPostProcessor/ProtectionILPostProcessor.cs
//
// This file is intentionally empty. The editor assembly no longer uses
// Mono.Cecil at all; all IL rewriting happens in the CodeProtection.CodeGen
// assembly during compilation.
