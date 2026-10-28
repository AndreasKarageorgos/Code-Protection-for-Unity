using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;

namespace CodeProtection.CodeGen
{
    
    /// Compile time obfuscator. Unlike a post build DLL rewrite, this runs during
    /// compilation, before IL2CPP converts the IL to C++, so Add Random Code and
    /// String Obfuscation take effect on BOTH Mono and IL2CPP builds out of the box.
    ///
    /// Safety design, because this runs on every compilation:
    ///   * It only touches player assemblies (editor and play mode compiles define
    ///     UNITY_EDITOR and are skipped), so the editor behaves normally and the
    ///     blast radius is the same as the old post build pass.
    ///   * It only touches Assembly-CSharp and Assembly-CSharp-firstpass.
    ///   * It does nothing unless the config file enables a feature, so it is inert
    ///     until you turn something on in Tools > Code Protection.
    ///   * Any failure returns the original assembly untouched. It never ships
    ///     corrupt IL.
    ///
    /// Kill switch: if this assembly ever fails to compile on your Unity version,
    /// delete the CodeProtection/ILPostProcessor folder. The rest of the asset
    /// keeps working; you only lose the IL2CPP route for these two passes.
    
    public sealed class ProtectionILPostProcessor : ILPostProcessor
    {
        public override ILPostProcessor GetInstance() => this;

        public override bool WillProcess(ICompiledAssembly compiledAssembly)
        {
            string name = compiledAssembly.Name;
            if (name != "Assembly-CSharp" && name != "Assembly-CSharp-firstpass") return false;

            var defines = compiledAssembly.Defines;
            if (defines != null && defines.Contains("UNITY_EDITOR")) return false; // editor / play mode

            var cfg = ProtectionConfig.Load();
            return cfg != null && (cfg.AddRandomCode || cfg.ObfuscateStrings);
        }

        public override ILPostProcessResult Process(ICompiledAssembly compiledAssembly)
        {
            if (!WillProcess(compiledAssembly)) return null;

            var cfg = ProtectionConfig.Load();
            if (cfg == null) return null;

            var messages = new List<DiagnosticMessage>();
            try
            {
                byte[] peData = compiledAssembly.InMemoryAssembly.PeData;

                using (var resolver = new PostProcessorAssemblyResolver(compiledAssembly))
                using (var peStream = new MemoryStream(peData))
                using (var asm = AssemblyDefinition.ReadAssembly(peStream, new ReaderParameters
                {
                    InMemory = true,
                    ReadSymbols = false,
                    AssemblyResolver = resolver
                }))
                {
                    bool changed = Rewriter.Run(asm.MainModule, cfg);
                    if (!changed) return new ILPostProcessResult(null, messages);

                    using (var outPe = new MemoryStream())
                    {
                        asm.Write(outPe, new WriterParameters { WriteSymbols = false });
                        var result = new InMemoryAssembly(outPe.ToArray(), new byte[0]);
                        return new ILPostProcessResult(result, messages);
                    }
                }
            }
            catch (Exception e)
            {
                // Never corrupt the build. Leave the assembly exactly as compiled.
                messages.Add(new DiagnosticMessage
                {
                    DiagnosticType = DiagnosticType.Warning,
                    MessageData = "[CodeProtection] ILPostProcessor skipped " +
                                  compiledAssembly.Name + ": " + e.Message
                });
                return new ILPostProcessResult(null, messages);
            }
        }
    }

    // =====================================================================
    //  Config (plain key=value file written by the editor panel)
    // =====================================================================

    internal sealed class ProtectionConfig
    {
        public bool AddRandomCode;
        public bool ObfuscateStrings;
        public bool ForceAll;
        public int JunkMethodsPerType = 4;
        public bool InjectDeadCalls = true;
        public int MinStringLength = 2;
        public List<string> ExcludeNamespaces = new List<string>();
        public string DecoderType = "CPStrings";
        public string DecoderMethod = "R";
        public string FieldPrefix = "cp_";
        public string MethodPrefix = "cp_";
        public int Salt = 0;

        public static ProtectionConfig Load()
        {
            try
            {
                string path = FindConfig();
                if (path == null || !File.Exists(path)) return null;

                var cfg = new ProtectionConfig();
                foreach (var raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "addRandomCode": cfg.AddRandomCode = v == "1"; break;
                        case "obfuscateStrings": cfg.ObfuscateStrings = v == "1"; break;
                        case "forceAll": cfg.ForceAll = v == "1"; break;
                        case "junkMethodsPerType": cfg.JunkMethodsPerType = ParseInt(v, 4); break;
                        case "injectDeadCalls": cfg.InjectDeadCalls = v == "1"; break;
                        case "minStringLength": cfg.MinStringLength = ParseInt(v, 2); break;
                        case "excludeNamespaces": cfg.ExcludeNamespaces = SplitList(v); break;
                        case "decoderType": if (IsIdent(v)) cfg.DecoderType = v; break;
                        case "decoderMethod": if (IsIdent(v)) cfg.DecoderMethod = v; break;
                        case "fieldPrefix": if (IsIdent(v)) cfg.FieldPrefix = v; break;
                        case "methodPrefix": if (IsIdent(v)) cfg.MethodPrefix = v; break;
                        case "salt": cfg.Salt = ParseInt(v, 0); break;
                    }
                }
                return cfg;
            }
            catch { return null; }
        }

        static string FindConfig()
        {
            string rel = Path.Combine("ProjectSettings", "CodeProtection.cfg");
            if (File.Exists(rel)) return rel;
            try
            {
                string abs = Path.Combine(Directory.GetCurrentDirectory(), rel);
                if (File.Exists(abs)) return abs;
            }
            catch { }
            return null;
        }

        static int ParseInt(string v, int fallback) => int.TryParse(v, out var r) ? r : fallback;

        static List<string> SplitList(string v)
        {
            var list = new List<string>();
            foreach (var p in v.Split(','))
            {
                var t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        static bool IsIdent(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
            foreach (var c in s)
                if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
            return true;
        }
    }

    // =====================================================================
    //  Rewriter (the actual Cecil passes, no engine dependency)
    // =====================================================================

    internal static class Rewriter
    {
        public static bool Run(ModuleDefinition module, ProtectionConfig cfg)
        {
            var rng = new System.Random(StableSeed(module.Assembly.Name.Name) ^ cfg.Salt);
            bool changed = false;

            var types = new List<TypeDefinition>(module.Types);

            MethodDefinition reveal = null;
            if (cfg.ObfuscateStrings)
                reveal = EnsureRevealMethod(module, cfg); // throws on failure -> caught upstream

            foreach (var type in types)
            {
                if (!ShouldProcess(type, cfg)) continue;
                try
                {
                    if (cfg.AddRandomCode) { InjectIntoType(module, type, cfg, rng); changed = true; }
                    if (cfg.ObfuscateStrings && reveal != null)
                    {
                        if (ObfuscateStringsInType(type, reveal, rng, cfg)) changed = true;
                    }
                }
                catch { /* skip an awkward type, keep the rest */ }
            }

            return changed;
        }

        static bool ShouldProcess(TypeDefinition type, ProtectionConfig cfg)
        {
            if (type.Name == "<Module>") return false;
            if (type.IsInterface) return false;
            if (HasAttr(type, "DoNotProtectAttribute")) return false;

            string ns = type.Namespace ?? "";
            foreach (var ex in cfg.ExcludeNamespaces)
                if (ns.StartsWith(ex, StringComparison.Ordinal)) return false;

            if (cfg.ForceAll) return true;
            return HasAttr(type, "ProtectAttribute");
        }

        // ---- Add Random Code ----

        static void InjectIntoType(ModuleDefinition module, TypeDefinition type,
                                   ProtectionConfig cfg, System.Random rng)
        {
            var flag = new FieldDefinition(
                cfg.FieldPrefix + RandName(rng),
                FieldAttributes.Static | FieldAttributes.Private | FieldAttributes.InitOnly,
                module.TypeSystem.Boolean);
            type.Fields.Add(flag);

            var junk = new List<MethodDefinition>();
            for (int i = 0; i < cfg.JunkMethodsPerType; i++)
            {
                var m = BuildJunkMethod(module, cfg, rng);
                type.Methods.Add(m);
                junk.Add(m);
            }

            if (cfg.InjectDeadCalls && junk.Count > 0)
            {
                foreach (var method in new List<MethodDefinition>(type.Methods))
                {
                    if (junk.Contains(method)) continue;
                    if (!method.HasBody) continue;
                    if (method.Body.Instructions.Count == 0) continue;
                    if (HasAttr(method, "DoNotProtectAttribute")) continue;
                    if (method.IsConstructor && method.IsStatic) continue;

                    try { InjectDeadCall(method, flag, junk[rng.Next(junk.Count)]); }
                    catch { }
                }
            }
        }

        static MethodDefinition BuildJunkMethod(ModuleDefinition module, ProtectionConfig cfg, System.Random rng)
        {
            var m = new MethodDefinition(
                cfg.MethodPrefix + RandName(rng),
                MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig,
                module.TypeSystem.Int32);

            var il = m.Body.GetILProcessor();
            il.Emit(OpCodes.Ldc_I4, rng.Next(1, 9999));
            int steps = rng.Next(3, 9);
            for (int i = 0; i < steps; i++)
            {
                il.Emit(OpCodes.Ldc_I4, rng.Next(1, 4096));
                switch (rng.Next(4))
                {
                    case 0: il.Emit(OpCodes.Add); break;
                    case 1: il.Emit(OpCodes.Mul); break;
                    case 2: il.Emit(OpCodes.Xor); break;
                    default: il.Emit(OpCodes.Or); break;
                }
            }
            il.Emit(OpCodes.Ret);
            return m;
        }

        static void InjectDeadCall(MethodDefinition method, FieldDefinition flag, MethodDefinition junk)
        {
            var il = method.Body.GetILProcessor();
            var first = method.Body.Instructions[0];

            var ldflag = il.Create(OpCodes.Ldsfld, flag);
            var brFalse = il.Create(OpCodes.Brfalse, first);
            var call = il.Create(OpCodes.Call, junk);
            var pop = il.Create(OpCodes.Pop);

            il.InsertBefore(first, ldflag);
            il.InsertBefore(first, brFalse);
            il.InsertBefore(first, call);
            il.InsertBefore(first, pop);
        }

        // ---- String Obfuscation ----

        static bool ObfuscateStringsInType(TypeDefinition type, MethodDefinition reveal,
                                           System.Random rng, ProtectionConfig cfg)
        {
            bool changed = false;
            foreach (var method in new List<MethodDefinition>(type.Methods))
            {
                if (!method.HasBody) continue;
                if (HasAttr(method, "DoNotProtectAttribute")) continue;

                var il = method.Body.GetILProcessor();
                var instructions = new List<Instruction>(method.Body.Instructions);
                foreach (var ins in instructions)
                {
                    if (ins.OpCode != OpCodes.Ldstr) continue;
                    var value = ins.Operand as string;
                    if (string.IsNullOrEmpty(value) || value.Length < cfg.MinStringLength) continue;

                    int key = rng.Next(1, 0x10000);
                    ins.Operand = EncryptString(value, key, cfg.Salt);

                    var ldKey = il.Create(OpCodes.Ldc_I4, key);
                    var callReveal = il.Create(OpCodes.Call, reveal);
                    il.InsertAfter(ins, callReveal);
                    il.InsertAfter(ins, ldKey);
                    changed = true;
                }
            }
            return changed;
        }

        static string EncryptString(string s, int key, int salt)
        {
            var a = s.ToCharArray();
            for (int i = 0; i < a.Length; i++)
                a[i] = (char)((a[i] ^ (key + i + salt)) & 0xFFFF);
            return new string(a);
        }

        
        /// Emits the decoder into the module and returns it. Equivalent to:
        ///   static string R(string s, int k) {
        ///       char[] a = s.ToCharArray();
        ///       for (int i = 0; i < a.Length; i++)
        ///           a[i] = (char)((a[i] ^ (k + i + SALT)) & 0xFFFF);
        ///       return new string(a);
        ///   }
        /// The type name, method name, and SALT all vary per project.
        
        static MethodDefinition EnsureRevealMethod(ModuleDefinition module, ProtectionConfig cfg)
        {
            var strTypeDef = module.TypeSystem.String.Resolve();
            if (strTypeDef == null) throw new Exception("cannot resolve System.String");

            MethodReference toCharArray = null, strCtor = null;
            foreach (var m in strTypeDef.Methods)
            {
                if (toCharArray == null && m.Name == "ToCharArray" && m.Parameters.Count == 0 && m.IsPublic)
                    toCharArray = module.ImportReference(m);
                if (strCtor == null && m.IsConstructor && m.Parameters.Count == 1 &&
                    m.Parameters[0].ParameterType.FullName == "System.Char[]")
                    strCtor = module.ImportReference(m);
            }
            if (toCharArray == null || strCtor == null)
                throw new Exception("cannot resolve string members");

            var holder = new TypeDefinition(
                "", cfg.DecoderType,
                TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Abstract | TypeAttributes.Class,
                module.TypeSystem.Object);
            module.Types.Add(holder);

            var int32 = module.TypeSystem.Int32;
            var charArray = new ArrayType(module.TypeSystem.Char);

            var method = new MethodDefinition(cfg.DecoderMethod,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                module.TypeSystem.String);
            method.Parameters.Add(new ParameterDefinition("s", ParameterAttributes.None, module.TypeSystem.String));
            method.Parameters.Add(new ParameterDefinition("k", ParameterAttributes.None, int32));
            holder.Methods.Add(method);

            method.Body.InitLocals = true;
            var vA = new VariableDefinition(charArray);
            var vI = new VariableDefinition(int32);
            method.Body.Variables.Add(vA);
            method.Body.Variables.Add(vI);

            var il = method.Body.GetILProcessor();

            var bLoadArr = Instruction.Create(OpCodes.Ldloc, vA); // body start (stelem target)
            var cLoadI = Instruction.Create(OpCodes.Ldloc, vI);   // condition start

            il.Append(Instruction.Create(OpCodes.Ldarg_0));
            il.Append(Instruction.Create(OpCodes.Callvirt, toCharArray));
            il.Append(Instruction.Create(OpCodes.Stloc, vA));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_0));
            il.Append(Instruction.Create(OpCodes.Stloc, vI));
            il.Append(Instruction.Create(OpCodes.Br, cLoadI));

            // a[i] = (char)((a[i] ^ (k + i + salt)) & 0xFFFF);
            il.Append(bLoadArr);                                 // a
            il.Append(Instruction.Create(OpCodes.Ldloc, vI));   // a, i
            il.Append(Instruction.Create(OpCodes.Ldloc, vA));   // a, i, a
            il.Append(Instruction.Create(OpCodes.Ldloc, vI));   // a, i, a, i
            il.Append(Instruction.Create(OpCodes.Ldelem_U2));   // a, i, ch
            il.Append(Instruction.Create(OpCodes.Ldarg_1));     // a, i, ch, k
            il.Append(Instruction.Create(OpCodes.Ldloc, vI));   // a, i, ch, k, i
            il.Append(Instruction.Create(OpCodes.Add));         // a, i, ch, k+i
            il.Append(Instruction.Create(OpCodes.Ldc_I4, cfg.Salt));
            il.Append(Instruction.Create(OpCodes.Add));         // a, i, ch, k+i+salt
            il.Append(Instruction.Create(OpCodes.Xor));         // a, i, masked-xor
            il.Append(Instruction.Create(OpCodes.Ldc_I4, 0xFFFF));
            il.Append(Instruction.Create(OpCodes.And));
            il.Append(Instruction.Create(OpCodes.Conv_U2));     // a, i, char
            il.Append(Instruction.Create(OpCodes.Stelem_I2));
            il.Append(Instruction.Create(OpCodes.Ldloc, vI));
            il.Append(Instruction.Create(OpCodes.Ldc_I4_1));
            il.Append(Instruction.Create(OpCodes.Add));
            il.Append(Instruction.Create(OpCodes.Stloc, vI));

            il.Append(cLoadI);                                  // i
            il.Append(Instruction.Create(OpCodes.Ldloc, vA));   // i, a
            il.Append(Instruction.Create(OpCodes.Ldlen));       // i, len
            il.Append(Instruction.Create(OpCodes.Conv_I4));     // i, len
            il.Append(Instruction.Create(OpCodes.Blt, bLoadArr));

            il.Append(Instruction.Create(OpCodes.Ldloc, vA));
            il.Append(Instruction.Create(OpCodes.Newobj, strCtor));
            il.Append(Instruction.Create(OpCodes.Ret));

            return method;
        }

        // ---- helpers ----

        static bool HasAttr(TypeDefinition type, string shortName)
        {
            foreach (var a in type.CustomAttributes)
                if (a.AttributeType.Name == shortName) return true;
            return false;
        }

        static bool HasAttr(MethodDefinition method, string shortName)
        {
            foreach (var a in method.CustomAttributes)
                if (a.AttributeType.Name == shortName) return true;
            return false;
        }

        static string RandName(System.Random rng)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
            var buf = new char[10];
            for (int i = 0; i < buf.Length; i++) buf[i] = chars[rng.Next(chars.Length)];
            return new string(buf);
        }

        static int StableSeed(string name)
        {
            int h = 17;
            foreach (char c in name) h = h * 31 + c;
            return h;
        }
    }

    // =====================================================================
    //  Assembly resolver for the ILPostProcessing context
    // =====================================================================

    internal sealed class PostProcessorAssemblyResolver : IAssemblyResolver
    {
        readonly string[] _references;
        readonly Dictionary<string, AssemblyDefinition> _cache = new Dictionary<string, AssemblyDefinition>();

        public PostProcessorAssemblyResolver(ICompiledAssembly compiledAssembly)
        {
            _references = compiledAssembly.References ?? new string[0];
        }

        public AssemblyDefinition Resolve(AssemblyNameReference name) =>
            Resolve(name, new ReaderParameters(ReadingMode.Deferred));

        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
        {
            if (name == null) return null;
            lock (_cache)
            {
                if (_cache.TryGetValue(name.Name, out var cached)) return cached;

                foreach (var path in _references)
                {
                    if (Path.GetFileNameWithoutExtension(path) != name.Name) continue;
                    try
                    {
                        parameters.AssemblyResolver = this;
                        var def = AssemblyDefinition.ReadAssembly(path, parameters);
                        _cache[name.Name] = def;
                        return def;
                    }
                    catch { return null; }
                }
                return null;
            }
        }

        public void Dispose()
        {
            lock (_cache)
            {
                foreach (var a in _cache.Values)
                {
                    try { a.Dispose(); } catch { }
                }
                _cache.Clear();
            }
        }
    }
}
