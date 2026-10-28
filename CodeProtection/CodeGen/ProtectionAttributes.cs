using System;

namespace CodeProtection
{
  
    /// Marks a class or struct to be included in the random code and string
    /// obfuscation passes. Equivalent to an inclusion list.

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
    public sealed class ProtectAttribute : Attribute { }

  
    /// Marks a class, struct, or method to be skipped by every pass, even when
    /// "Force" is on. Use it on anything timing sensitive, on
    /// [RuntimeInitializeOnLoadMethod], on serialization callbacks, and on code
    /// reached through reflection or that relies on string reference identity.

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method, Inherited = false)]
    public sealed class DoNotProtectAttribute : Attribute { }
}
