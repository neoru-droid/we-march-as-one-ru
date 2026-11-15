using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

[assembly: System.Reflection.AssemblyTitle("We March as One RU Assembly Patcher")]
[assembly: System.Reflection.AssemblyDescription("Assembly patcher for We March as One Russian localization")]
[assembly: System.Reflection.AssemblyCompany("neoru-droid")]
[assembly: System.Reflection.AssemblyProduct("We March as One RU Localization")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 neoru-droid")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

public static class Program
{
    public static int Main(string[] args)
    {
        if (args == null || args.Length < 1)
        {
            PrintUsage();
            return 1;
        }

        string cmd = args[0];
        try
        {
            if (cmd == "prep-ref" && args.Length == 4)
            {
                PrepareCompileReference(args[1], args[2], args[3]);
                return 0;
            }

            if (cmd == "patch")
            {
                bool includeAchievements = true;
                List<string> positional = new List<string>();
                for (int i = 1; i < args.Length; i++)
                {
                    string arg = args[i];
                    if (string.Equals(arg, "--no-achievements", StringComparison.OrdinalIgnoreCase))
                    {
                        includeAchievements = false;
                    }
                    else if (string.Equals(arg, "--achievements", StringComparison.OrdinalIgnoreCase))
                    {
                        includeAchievements = true;
                    }
                    else if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine("Unknown option: " + arg);
                        PrintUsage();
                        return 1;
                    }
                    else
                    {
                        positional.Add(arg);
                    }
                }

                if (positional.Count != 4)
                {
                    PrintUsage();
                    return 1;
                }

                ApplyPatch(positional[0], positional[1], positional[2], positional[3], includeAchievements);
                return 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }

        PrintUsage();
        return 1;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  Patcher.exe prep-ref <origAssemblyCSharp> <managedDir> <outRefDll>");
        Console.Error.WriteLine("  Patcher.exe patch <origAssemblyCSharp> <ruModRuntimeDll> <managedDir> <outAssemblyCSharp> [--no-achievements]");
    }

    private static void EnsureParentDirectory(string filePath)
    {
        string full = Path.GetFullPath(filePath);
        string dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static DefaultAssemblyResolver CreateResolver(string managedDir)
    {
        DefaultAssemblyResolver resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(managedDir);
        return resolver;
    }

    public static void PrepareCompileReference(string origPath, string managedDir, string outRefPath)
    {
        byte[] origBytes = File.ReadAllBytes(origPath);
        using (DefaultAssemblyResolver resolver = CreateResolver(managedDir))
        using (MemoryStream srcStream = new MemoryStream(origBytes, false))
        using (AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(
            srcStream,
            new ReaderParameters { AssemblyResolver = resolver }))
        {
            ModuleDefinition mod = asm.MainModule;
            MethodReference ctor = mod.ImportReference(
                typeof(InternalsVisibleToAttribute).GetConstructor(new Type[] { typeof(string) }));
            CustomAttribute attr = new CustomAttribute(ctor);
            attr.ConstructorArguments.Add(
                new CustomAttributeArgument(mod.TypeSystem.String, "RuModRuntime"));
            asm.CustomAttributes.Add(attr);

            EnsureParentDirectory(outRefPath);
            asm.Write(outRefPath);
        }
        Console.WriteLine("Prepared compile reference: " + outRefPath);
    }

    public static bool ApplyPatch(
        string origPath,
        string runtimeDllPath,
        string managedDir,
        string outPath,
        bool includeAchievements)
    {
        byte[] origBytes = File.ReadAllBytes(origPath);
        byte[] runtimeBytes = File.ReadAllBytes(runtimeDllPath);

        bool appliedAchievements = includeAchievements;
        byte[] patchedBytes;

        if (includeAchievements)
        {
            try
            {
                patchedBytes = BuildPatchedBytes(origBytes, runtimeBytes, managedDir, true);
                VerifyOutputBytes(patchedBytes, managedDir, true);
            }
            catch (AchievementsHookException ex)
            {
                Console.WriteLine(
                    "WARNING: Optional Achievements UI hook skipped (" +
                    ex.Message +
                    "); falling back to --no-achievements (Cyrillic multi-atlas font fix only).");
                appliedAchievements = false;
                patchedBytes = BuildPatchedBytes(origBytes, runtimeBytes, managedDir, false);
                VerifyOutputBytes(patchedBytes, managedDir, false);
            }
        }
        else
        {
            patchedBytes = BuildPatchedBytes(origBytes, runtimeBytes, managedDir, false);
            VerifyOutputBytes(patchedBytes, managedDir, false);
        }

        EnsureParentDirectory(outPath);
        File.WriteAllBytes(outPath, patchedBytes);
        Console.WriteLine(
            "Patched Assembly-CSharp written to: " +
            outPath +
            (appliedAchievements ? " (mode: full)" : " (mode: no-achievements)"));
        return appliedAchievements;
    }

    private sealed class AchievementsHookException : Exception
    {
        public AchievementsHookException(string message, Exception inner)
            : base(message, inner)
        {
        }

        public AchievementsHookException(string message)
            : base(message)
        {
        }
    }

    private sealed class ModuleCloner
    {
        private readonly ModuleDefinition _source;
        private readonly ModuleDefinition _target;
        private readonly Dictionary<string, TypeDefinition> _clonedTypes =
            new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        private readonly Dictionary<FieldDefinition, FieldDefinition> _clonedFields =
            new Dictionary<FieldDefinition, FieldDefinition>();
        private readonly Dictionary<MethodDefinition, MethodDefinition> _clonedMethods =
            new Dictionary<MethodDefinition, MethodDefinition>();

        public ModuleCloner(ModuleDefinition source, ModuleDefinition target)
        {
            _source = source;
            _target = target;
        }

        public MethodDefinition GetClonedMethod(string typeFullName, string methodName)
        {
            TypeDefinition td;
            if (!_clonedTypes.TryGetValue(typeFullName, out td))
            {
                throw new InvalidOperationException("Missing cloned type: " + typeFullName);
            }
            foreach (MethodDefinition md in td.Methods)
            {
                if (md.Name == methodName)
                {
                    return md;
                }
            }
            throw new InvalidOperationException("Missing cloned method: " + typeFullName + "::" + methodName);
        }

        public void CloneSelectedTypes(Predicate<TypeDefinition> shouldIncludeTopType)
        {
            List<TypeDefinition> selectedSourceTypes = new List<TypeDefinition>();
            foreach (TypeDefinition top in _source.Types)
            {
                if (top.Name == "<Module>" ||
                    top.Namespace == "Microsoft.CodeAnalysis" ||
                    top.Namespace == "System.Runtime.CompilerServices")
                {
                    continue;
                }
                if (!shouldIncludeTopType(top) || _clonedTypes.ContainsKey(top.FullName))
                {
                    continue;
                }

                string newName = top.Name == "<PrivateImplementationDetails>"
                    ? "<RuModPrivateImplementationDetails>"
                    : top.Name;
                TypeDefinition clonedTop = new TypeDefinition(top.Namespace, newName, top.Attributes);
                clonedTop.ClassSize = top.ClassSize;
                clonedTop.PackingSize = top.PackingSize;
                _target.Types.Add(clonedTop);
                _clonedTypes[top.FullName] = clonedTop;
                selectedSourceTypes.Add(top);
                CreateNestedShells(top, clonedTop, selectedSourceTypes);
            }

            foreach (TypeDefinition srcType in selectedSourceTypes)
            {
                TypeDefinition dstType = _clonedTypes[srcType.FullName];
                if (srcType.BaseType != null)
                {
                    dstType.BaseType = MapTypeRef(srcType.BaseType);
                }
                foreach (InterfaceImplementation iface in srcType.Interfaces)
                {
                    dstType.Interfaces.Add(
                        new InterfaceImplementation(MapTypeRef(iface.InterfaceType)));
                }
                CopyCustomAttributes(srcType.CustomAttributes, dstType.CustomAttributes);

                foreach (FieldDefinition sf in srcType.Fields)
                {
                    FieldDefinition df = new FieldDefinition(sf.Name, sf.Attributes, MapTypeRef(sf.FieldType));
                    if (sf.HasConstant)
                    {
                        df.Constant = sf.Constant;
                    }
                    if (sf.InitialValue != null && sf.InitialValue.Length > 0)
                    {
                        byte[] copy = new byte[sf.InitialValue.Length];
                        Buffer.BlockCopy(sf.InitialValue, 0, copy, 0, copy.Length);
                        df.InitialValue = copy;
                    }
                    CopyCustomAttributes(sf.CustomAttributes, df.CustomAttributes);
                    dstType.Fields.Add(df);
                    _clonedFields[sf] = df;
                }

                foreach (MethodDefinition sm in srcType.Methods)
                {
                    MethodDefinition dm = new MethodDefinition(
                        sm.Name,
                        sm.Attributes,
                        MapTypeRef(sm.ReturnType));
                    dm.ImplAttributes = sm.ImplAttributes;
                    dm.SemanticsAttributes = sm.SemanticsAttributes;
                    foreach (ParameterDefinition sp in sm.Parameters)
                    {
                        ParameterDefinition dp = new ParameterDefinition(
                            sp.Name,
                            sp.Attributes,
                            MapTypeRef(sp.ParameterType));
                        if (sp.HasConstant)
                        {
                            dp.Constant = sp.Constant;
                        }
                        CopyCustomAttributes(sp.CustomAttributes, dp.CustomAttributes);
                        dm.Parameters.Add(dp);
                    }
                    CopyCustomAttributes(sm.CustomAttributes, dm.CustomAttributes);
                    dstType.Methods.Add(dm);
                    _clonedMethods[sm] = dm;
                }

                foreach (PropertyDefinition sprop in srcType.Properties)
                {
                    PropertyDefinition dprop = new PropertyDefinition(
                        sprop.Name,
                        sprop.Attributes,
                        MapTypeRef(sprop.PropertyType));
                    if (sprop.GetMethod != null)
                    {
                        dprop.GetMethod = _clonedMethods[sprop.GetMethod];
                    }
                    if (sprop.SetMethod != null)
                    {
                        dprop.SetMethod = _clonedMethods[sprop.SetMethod];
                    }
                    dstType.Properties.Add(dprop);
                }
            }

            foreach (TypeDefinition srcType in selectedSourceTypes)
            {
                foreach (MethodDefinition sm in srcType.Methods)
                {
                    if (sm.HasBody)
                    {
                        CloneMethodBody(sm, _clonedMethods[sm]);
                    }
                }
            }
        }

        private void CreateNestedShells(
            TypeDefinition srcParent,
            TypeDefinition dstParent,
            List<TypeDefinition> accum)
        {
            foreach (TypeDefinition sn in srcParent.NestedTypes)
            {
                TypeDefinition dn = new TypeDefinition(sn.Namespace, sn.Name, sn.Attributes);
                dn.ClassSize = sn.ClassSize;
                dn.PackingSize = sn.PackingSize;
                dstParent.NestedTypes.Add(dn);
                _clonedTypes[sn.FullName] = dn;
                accum.Add(sn);
                CreateNestedShells(sn, dn, accum);
            }
        }

        private void CopyCustomAttributes(
            Mono.Collections.Generic.Collection<CustomAttribute> srcList,
            Mono.Collections.Generic.Collection<CustomAttribute> dstList)
        {
            foreach (CustomAttribute ca in srcList)
            {
                string attrName = ca.AttributeType.FullName;
                if (attrName.StartsWith("System.Runtime.CompilerServices.", StringComparison.Ordinal) ||
                    attrName.StartsWith("Microsoft.CodeAnalysis.", StringComparison.Ordinal))
                {
                    continue;
                }
                CustomAttribute copy = new CustomAttribute(MapMethodRef(ca.Constructor));
                foreach (CustomAttributeArgument arg in ca.ConstructorArguments)
                {
                    copy.ConstructorArguments.Add(
                        new CustomAttributeArgument(MapTypeRef(arg.Type), MapAttrValue(arg.Value)));
                }
                foreach (CustomAttributeNamedArgument farg in ca.Fields)
                {
                    copy.Fields.Add(
                        new CustomAttributeNamedArgument(
                            farg.Name,
                            new CustomAttributeArgument(
                                MapTypeRef(farg.Argument.Type),
                                MapAttrValue(farg.Argument.Value))));
                }
                foreach (CustomAttributeNamedArgument parg in ca.Properties)
                {
                    copy.Properties.Add(
                        new CustomAttributeNamedArgument(
                            parg.Name,
                            new CustomAttributeArgument(
                                MapTypeRef(parg.Argument.Type),
                                MapAttrValue(parg.Argument.Value))));
                }
                dstList.Add(copy);
            }
        }

        private object MapAttrValue(object val)
        {
            TypeReference tr = val as TypeReference;
            if (tr != null)
            {
                return MapTypeRef(tr);
            }
            return val;
        }

        private TypeDefinition FindInTarget(TypeReference tr)
        {
            if (tr.DeclaringType != null)
            {
                TypeDefinition parent = FindInTarget(tr.DeclaringType);
                if (parent == null)
                {
                    return null;
                }
                foreach (TypeDefinition nested in parent.NestedTypes)
                {
                    if (nested.Name == tr.Name)
                    {
                        return nested;
                    }
                }
                return null;
            }
            return _target.GetType(tr.Namespace, tr.Name);
        }

        private TypeReference MapTypeRef(TypeReference tr)
        {
            if (tr == null)
            {
                return null;
            }
            if (tr is ArrayType)
            {
                ArrayType at = (ArrayType)tr;
                return new ArrayType(MapTypeRef(at.ElementType), at.Rank);
            }
            if (tr is ByReferenceType)
            {
                ByReferenceType br = (ByReferenceType)tr;
                return new ByReferenceType(MapTypeRef(br.ElementType));
            }
            if (tr is PointerType)
            {
                PointerType pt = (PointerType)tr;
                return new PointerType(MapTypeRef(pt.ElementType));
            }

            TypeDefinition cloned;
            if (_clonedTypes.TryGetValue(tr.FullName, out cloned))
            {
                return cloned;
            }

            TypeDefinition localTarget = FindInTarget(tr);
            if (localTarget != null)
            {
                return localTarget;
            }

            return _target.ImportReference(tr);
        }

        private FieldReference MapFieldRef(FieldReference fr)
        {
            FieldDefinition fd = fr as FieldDefinition;
            if (fd != null && _clonedFields.ContainsKey(fd))
            {
                return _clonedFields[fd];
            }

            TypeDefinition clonedDecl;
            if (_clonedTypes.TryGetValue(fr.DeclaringType.FullName, out clonedDecl))
            {
                foreach (FieldDefinition cand in clonedDecl.Fields)
                {
                    if (cand.Name == fr.Name)
                    {
                        return cand;
                    }
                }
                throw new InvalidOperationException("Unmapped cloned field: " + fr.FullName);
            }

            TypeDefinition localDecl = FindInTarget(fr.DeclaringType);
            if (localDecl != null)
            {
                string expectedType = MapTypeRef(fr.FieldType).FullName;
                foreach (FieldDefinition cand in localDecl.Fields)
                {
                    if (cand.Name == fr.Name && cand.FieldType.FullName == expectedType)
                    {
                        return cand;
                    }
                }
                throw new InvalidOperationException("Missing or incompatible target field: " + fr.FullName);
            }

            return _target.ImportReference(fr);
        }

        private MethodReference MapMethodRef(MethodReference mr)
        {
            MethodDefinition md = mr as MethodDefinition;
            if (md != null && _clonedMethods.ContainsKey(md))
            {
                return _clonedMethods[md];
            }

            TypeDefinition clonedDecl;
            if (_clonedTypes.TryGetValue(mr.DeclaringType.FullName, out clonedDecl))
            {
                MethodDefinition match = MatchMethod(clonedDecl, mr);
                if (match != null)
                {
                    return match;
                }
                throw new InvalidOperationException("Unmapped cloned method: " + mr.FullName);
            }

            TypeDefinition localDecl = FindInTarget(mr.DeclaringType);
            if (localDecl != null)
            {
                MethodDefinition match = MatchMethod(localDecl, mr);
                if (match != null)
                {
                    return match;
                }
                throw new InvalidOperationException("Missing or incompatible target method: " + mr.FullName);
            }

            return _target.ImportReference(mr);
        }

        private MethodDefinition MatchMethod(TypeDefinition decl, MethodReference mr)
        {
            string expectedReturn = MapTypeRef(mr.ReturnType).FullName;
            foreach (MethodDefinition cand in decl.Methods)
            {
                if (cand.Name != mr.Name ||
                    cand.HasThis != mr.HasThis ||
                    cand.Parameters.Count != mr.Parameters.Count ||
                    cand.ReturnType.FullName != expectedReturn)
                {
                    continue;
                }
                bool same = true;
                for (int i = 0; i < cand.Parameters.Count; i++)
                {
                    string a = MapTypeRef(mr.Parameters[i].ParameterType).FullName;
                    string b = cand.Parameters[i].ParameterType.FullName;
                    if (a != b)
                    {
                        same = false;
                        break;
                    }
                }
                if (same)
                {
                    return cand;
                }
            }
            return null;
        }

        private void CloneMethodBody(MethodDefinition sm, MethodDefinition dm)
        {
            MethodBody sb = sm.Body;
            MethodBody db = dm.Body;
            db.InitLocals = sb.InitLocals;
            db.MaxStackSize = sb.MaxStackSize;

            Dictionary<VariableDefinition, VariableDefinition> varMap =
                new Dictionary<VariableDefinition, VariableDefinition>();
            foreach (VariableDefinition sv in sb.Variables)
            {
                VariableDefinition dv = new VariableDefinition(MapTypeRef(sv.VariableType));
                db.Variables.Add(dv);
                varMap[sv] = dv;
            }

            Dictionary<Instruction, Instruction> insMap =
                new Dictionary<Instruction, Instruction>();
            ILProcessor il = db.GetILProcessor();

            foreach (Instruction si in sb.Instructions)
            {
                Instruction di = CloneInstructionFirstPass(il, si, sm, dm, varMap);
                insMap[si] = di;
                db.Instructions.Add(di);
            }

            foreach (Instruction si in sb.Instructions)
            {
                Instruction di = insMap[si];
                Instruction targetIns = si.Operand as Instruction;
                if (targetIns != null)
                {
                    di.Operand = insMap[targetIns];
                    continue;
                }
                Instruction[] switchTargets = si.Operand as Instruction[];
                if (switchTargets != null)
                {
                    Instruction[] mapped = new Instruction[switchTargets.Length];
                    for (int i = 0; i < switchTargets.Length; i++)
                    {
                        mapped[i] = insMap[switchTargets[i]];
                    }
                    di.Operand = mapped;
                }
            }

            foreach (ExceptionHandler seh in sb.ExceptionHandlers)
            {
                ExceptionHandler deh = new ExceptionHandler(seh.HandlerType);
                if (seh.TryStart != null) deh.TryStart = insMap[seh.TryStart];
                if (seh.TryEnd != null) deh.TryEnd = insMap[seh.TryEnd];
                if (seh.HandlerStart != null) deh.HandlerStart = insMap[seh.HandlerStart];
                if (seh.HandlerEnd != null) deh.HandlerEnd = insMap[seh.HandlerEnd];
                if (seh.FilterStart != null) deh.FilterStart = insMap[seh.FilterStart];
                if (seh.CatchType != null) deh.CatchType = MapTypeRef(seh.CatchType);
                db.ExceptionHandlers.Add(deh);
            }
        }

        private Instruction CloneInstructionFirstPass(
            ILProcessor il,
            Instruction si,
            MethodDefinition sm,
            MethodDefinition dm,
            Dictionary<VariableDefinition, VariableDefinition> varMap)
        {
            OpCode op = si.OpCode;
            object operand = si.Operand;
            if (operand == null)
            {
                return il.Create(op);
            }
            if (operand is TypeReference)
            {
                return il.Create(op, MapTypeRef((TypeReference)operand));
            }
            if (operand is FieldReference)
            {
                return il.Create(op, MapFieldRef((FieldReference)operand));
            }
            if (operand is MethodReference)
            {
                return il.Create(op, MapMethodRef((MethodReference)operand));
            }
            if (operand is VariableDefinition)
            {
                return il.Create(op, varMap[(VariableDefinition)operand]);
            }
            if (operand is ParameterDefinition)
            {
                ParameterDefinition sp = (ParameterDefinition)operand;
                int idx = sm.Parameters.IndexOf(sp);
                return il.Create(op, dm.Parameters[idx]);
            }
            if (operand is string)
            {
                return il.Create(op, (string)operand);
            }
            if (operand is int)
            {
                return il.Create(op, (int)operand);
            }
            if (operand is sbyte)
            {
                return il.Create(op, (sbyte)operand);
            }
            if (operand is byte)
            {
                return il.Create(op, (byte)operand);
            }
            if (operand is float)
            {
                return il.Create(op, (float)operand);
            }
            if (operand is double)
            {
                return il.Create(op, (double)operand);
            }
            if (operand is long)
            {
                return il.Create(op, (long)operand);
            }
            if (operand is Instruction)
            {
                return il.Create(op, si);
            }
            if (operand is Instruction[])
            {
                return il.Create(op, (Instruction[])operand);
            }
            throw new NotSupportedException("Unsupported IL operand: " + operand.GetType().FullName);
        }
    }

    private static MethodDefinition RequireMethod(ModuleDefinition mod, string typeName, string methodName)
    {
        TypeDefinition td = mod.GetType(typeName);
        if (td == null)
        {
            throw new InvalidOperationException("Type not found: " + typeName);
        }
        foreach (MethodDefinition md in td.Methods)
        {
            if (md.Name == methodName)
            {
                return md;
            }
        }
        throw new InvalidOperationException("Method not found: " + typeName + "::" + methodName);
    }

    private static byte[] BuildPatchedBytes(
        byte[] origBytes,
        byte[] runtimeBytes,
        string managedDir,
        bool includeAchievements)
    {
        using (DefaultAssemblyResolver resolver = CreateResolver(managedDir))
        using (MemoryStream origStream = new MemoryStream(origBytes, false))
        using (MemoryStream runtimeStream = new MemoryStream(runtimeBytes, false))
        using (AssemblyDefinition targetAsm = AssemblyDefinition.ReadAssembly(
            origStream,
            new ReaderParameters { AssemblyResolver = resolver }))
        using (AssemblyDefinition runtimeAsm = AssemblyDefinition.ReadAssembly(
            runtimeStream,
            new ReaderParameters { AssemblyResolver = resolver }))
        {
            ModuleDefinition mod = targetAsm.MainModule;
            if (mod.GetType("ShapeWar.RuModHooks") != null)
            {
                throw new InvalidOperationException("Target Assembly-CSharp.dll is already patched.");
            }

            ModuleCloner cloner = new ModuleCloner(runtimeAsm.MainModule, mod);

            // 1. Core Cyrillic multi-atlas TMP font fix (always active)
            cloner.CloneSelectedTypes(delegate(TypeDefinition top)
            {
                return top.FullName == "ShapeWar.RuModHooks";
            });
            MethodDefinition fnCyrillicFont = cloner.GetClonedMethod(
                "ShapeWar.RuModHooks",
                "CreateCyrillicFontAsset");
            ApplyFontPatch(mod, fnCyrillicFont);

            // 2. Optional native in-game Achievements screen
            if (includeAchievements)
            {
                try
                {
                    cloner.CloneSelectedTypes(delegate(TypeDefinition top)
                    {
                        return top.FullName != "ShapeWar.RuModHooks";
                    });
                    ApplyAchievementsHooks(mod, cloner);
                }
                catch (Exception ex)
                {
                    throw new AchievementsHookException(ex.Message, ex);
                }
            }

            using (MemoryStream outStream = new MemoryStream())
            {
                targetAsm.Write(outStream);
                return outStream.ToArray();
            }
        }
    }

    private static void ApplyFontPatch(ModuleDefinition mod, MethodDefinition fnCyrillicFont)
    {
        TypeDefinition uiStyle = mod.GetType("ShapeWar.UIStyle");
        if (uiStyle == null)
        {
            throw new InvalidOperationException("Type not found: ShapeWar.UIStyle");
        }

        int countFont = ReplaceFontCreateCallsInMethod(FindMethodOrNull(uiStyle, "Get"), fnCyrillicFont);
        if (countFont == 0)
        {
            foreach (MethodDefinition md in uiStyle.Methods)
            {
                countFont += ReplaceFontCreateCallsInMethod(md, fnCyrillicFont);
            }
        }

        if (countFont < 1)
        {
            throw new InvalidOperationException(
                "Expected TMP_FontAsset.CreateFontAsset(Font) call in ShapeWar.UIStyle, found 0.");
        }
    }

    private static MethodDefinition FindMethodOrNull(TypeDefinition td, string methodName)
    {
        foreach (MethodDefinition md in td.Methods)
        {
            if (md.Name == methodName)
            {
                return md;
            }
        }
        return null;
    }

    private static int ReplaceFontCreateCallsInMethod(MethodDefinition md, MethodDefinition fnCyrillicFont)
    {
        if (md == null || !md.HasBody)
        {
            return 0;
        }
        int replaced = 0;
        foreach (Instruction ins in md.Body.Instructions)
        {
            MethodReference mr = ins.Operand as MethodReference;
            if (ins.OpCode == OpCodes.Call &&
                mr != null &&
                mr.DeclaringType.FullName == "TMPro.TMP_FontAsset" &&
                mr.Name == "CreateFontAsset" &&
                mr.Parameters.Count == 1 &&
                mr.Parameters[0].ParameterType.FullName == "UnityEngine.Font" &&
                mr.ReturnType.FullName == "TMPro.TMP_FontAsset")
            {
                ins.Operand = fnCyrillicFont;
                replaced++;
            }
        }
        return replaced;
    }

    private static void ApplyAchievementsHooks(ModuleDefinition mod, ModuleCloner cloner)
    {
        MethodDefinition fnBlocksEsc = cloner.GetClonedMethod(
            "ShapeWar.RuAchievementsHooks",
            "IsSkillTreeOrModalBlockingEsc");
        MethodDefinition fnMainMenuBuild = cloner.GetClonedMethod(
            "ShapeWar.RuAchievementsHooks",
            "AfterMainMenuBuild");
        MethodDefinition fnModeHomeSync = cloner.GetClonedMethod(
            "ShapeWar.RuAchievementsHooks",
            "FitModeHomeAndSyncAchievements");

        // 1. SkillTreeUI::get_BlocksEsc -> IsSkillTreeOrModalBlockingEsc
        MethodDefinition blocksEsc = RequireMethod(mod, "ShapeWar.SkillTreeUI", "get_BlocksEsc");
        if (!blocksEsc.IsStatic ||
            blocksEsc.Parameters.Count != 0 ||
            blocksEsc.ReturnType.FullName != "System.Boolean" ||
            !blocksEsc.HasBody)
        {
            throw new InvalidOperationException("Unexpected signature on ShapeWar.SkillTreeUI::get_BlocksEsc");
        }
        int countEsc = 0;
        foreach (Instruction ins in blocksEsc.Body.Instructions)
        {
            MethodReference mr = ins.Operand as MethodReference;
            if (ins.OpCode == OpCodes.Call &&
                mr != null &&
                mr.DeclaringType.FullName == "ShapeWar.SkillTreeUI" &&
                mr.Name == "get_IsOpen" &&
                mr.Parameters.Count == 0 &&
                mr.ReturnType.FullName == "System.Boolean")
            {
                ins.Operand = fnBlocksEsc;
                countEsc++;
            }
        }
        if (countEsc != 1)
        {
            throw new InvalidOperationException(
                "Expected 1 SkillTreeUI::get_IsOpen call in get_BlocksEsc, found " + countEsc);
        }

        // 2. MainMenuUI::Awake -> append AfterMainMenuBuild(this) before ret
        MethodDefinition mainAwake = RequireMethod(mod, "ShapeWar.MainMenuUI", "Awake");
        if (mainAwake.IsStatic ||
            mainAwake.Parameters.Count != 0 ||
            mainAwake.ReturnType.FullName != "System.Void" ||
            !mainAwake.HasBody ||
            mainAwake.Body.Instructions.Count == 0)
        {
            throw new InvalidOperationException("Unexpected signature on ShapeWar.MainMenuUI::Awake");
        }
        mainAwake.Body.SimplifyMacros();
        ILProcessor mainIl = mainAwake.Body.GetILProcessor();
        Instruction lastRet = mainAwake.Body.Instructions[mainAwake.Body.Instructions.Count - 1];
        if (lastRet.OpCode != OpCodes.Ret)
        {
            throw new InvalidOperationException("Expected MainMenuUI::Awake to end with ret");
        }
        mainIl.InsertBefore(lastRet, mainIl.Create(OpCodes.Ldarg_0));
        mainIl.InsertBefore(lastRet, mainIl.Create(OpCodes.Call, fnMainMenuBuild));
        mainAwake.Body.OptimizeMacros();

        // 3. ModeSelectUI::ApplyStyle -> FitModeHomeAndSyncAchievements
        MethodDefinition modeStyle = RequireMethod(mod, "ShapeWar.ModeSelectUI", "ApplyStyle");
        if (modeStyle.IsStatic || !modeStyle.HasBody)
        {
            throw new InvalidOperationException("Unexpected signature on ShapeWar.ModeSelectUI::ApplyStyle");
        }
        int countModeHome = 0;
        for (int i = 2; i < modeStyle.Body.Instructions.Count; i++)
        {
            Instruction prev2 = modeStyle.Body.Instructions[i - 2];
            Instruction cur = modeStyle.Body.Instructions[i];
            FieldReference fr = prev2.Operand as FieldReference;
            MethodReference mr = cur.Operand as MethodReference;
            if (fr != null &&
                fr.Name == "_home" &&
                fr.FieldType.FullName == "ShapeWar.UIPieceButton" &&
                cur.OpCode == OpCodes.Callvirt &&
                mr != null &&
                mr.DeclaringType.FullName == "ShapeWar.UIPieceButton" &&
                mr.Name == "FitWidth" &&
                mr.Parameters.Count == 1 &&
                mr.Parameters[0].ParameterType.FullName == "System.Single" &&
                mr.ReturnType.FullName == "ShapeWar.UIPieceButton")
            {
                cur.OpCode = OpCodes.Call;
                cur.Operand = fnModeHomeSync;
                countModeHome++;
            }
        }
        if (countModeHome != 1)
        {
            throw new InvalidOperationException(
                "Expected 1 _home.FitWidth call in ModeSelectUI::ApplyStyle, found " + countModeHome);
        }
    }

    private static void VerifyOutputBytes(byte[] patchedBytes, string managedDir, bool expectAchievements)
    {
        using (DefaultAssemblyResolver resolver = CreateResolver(managedDir))
        using (MemoryStream stream = new MemoryStream(patchedBytes, false))
        using (AssemblyDefinition asm = AssemblyDefinition.ReadAssembly(
            stream,
            new ReaderParameters { AssemblyResolver = resolver }))
        {
            ModuleDefinition mod = asm.MainModule;
            foreach (AssemblyNameReference ar in mod.AssemblyReferences)
            {
                if (ar.Name == "RuModRuntime" || ar.Name == "Assembly-CSharp")
                {
                    throw new InvalidOperationException("Illegal assembly reference in output: " + ar.Name);
                }
            }

            TypeDefinition fontHooks = mod.GetType("ShapeWar.RuModHooks");
            if (fontHooks == null)
            {
                throw new InvalidOperationException("Missing ShapeWar.RuModHooks in output Assembly-CSharp.dll");
            }

            TypeDefinition achHooks = mod.GetType("ShapeWar.RuAchievementsHooks");
            TypeDefinition achSync = mod.GetType("ShapeWar.RuModeAchButtonSync");
            TypeDefinition achModal = mod.GetType("ShapeWar.RuAchievementsModal");

            if (expectAchievements)
            {
                if (achHooks == null || achSync == null || achModal == null)
                {
                    throw new InvalidOperationException("Missing Achievements types in full mode output.");
                }
            }
            else
            {
                if (achHooks != null || achSync != null || achModal != null)
                {
                    throw new InvalidOperationException("Unexpected Achievements types present in --no-achievements output.");
                }
            }

            List<TypeDefinition> checkTypes = new List<TypeDefinition>();
            checkTypes.Add(fontHooks);
            AddIfPresent(checkTypes, mod.GetType("ShapeWar.UIStyle"));

            if (expectAchievements)
            {
                AddIfPresent(checkTypes, mod.GetType("ShapeWar.SkillTreeUI"));
                AddIfPresent(checkTypes, mod.GetType("ShapeWar.MainMenuUI"));
                AddIfPresent(checkTypes, mod.GetType("ShapeWar.ModeSelectUI"));
                checkTypes.Add(achHooks);
                checkTypes.Add(achSync);
                checkTypes.Add(achModal);
                foreach (TypeDefinition nested in achModal.NestedTypes)
                {
                    checkTypes.Add(nested);
                }
            }

            AssertOnlyAllowedHooksInModule(mod, expectAchievements);

            int verifiedRefs = 0;
            foreach (TypeDefinition td in checkTypes)
            {
                foreach (MethodDefinition md in td.Methods)
                {
                    if (!md.HasBody)
                    {
                        continue;
                    }
                    foreach (Instruction ins in md.Body.Instructions)
                    {
                        MethodReference mr = ins.Operand as MethodReference;
                        if (mr != null)
                        {
                            MethodDefinition resolved = mr.Resolve();
                            if (resolved == null)
                            {
                                throw new InvalidOperationException(
                                    "Unresolvable method ref in " + md.FullName + ": " + mr.FullName);
                            }
                            verifiedRefs++;
                        }
                        FieldReference fr = ins.Operand as FieldReference;
                        if (fr != null)
                        {
                            FieldDefinition resolvedF = fr.Resolve();
                            if (resolvedF == null)
                            {
                                throw new InvalidOperationException(
                                    "Unresolvable field ref in " + md.FullName + ": " + fr.FullName);
                            }
                            verifiedRefs++;
                        }
                    }
                }
            }
            Console.WriteLine(
                "Verification PASSED (" +
                verifiedRefs +
                " member references resolved cleanly, 0 illegal assembly refs).");
        }
    }

    private static void AddIfPresent(List<TypeDefinition> list, TypeDefinition td)
    {
        if (td != null)
        {
            list.Add(td);
        }
    }

    private static bool IsInjectedModType(string fullName)
    {
        return fullName == "ShapeWar.RuModHooks" ||
               fullName == "ShapeWar.RuAchievementsHooks" ||
               fullName == "ShapeWar.RuModeAchButtonSync" ||
               fullName == "ShapeWar.RuAchievementsModal" ||
               fullName.StartsWith("ShapeWar.RuAchievementsModal/", StringComparison.Ordinal) ||
               fullName == "<RuModPrivateImplementationDetails>";
    }

    private static void AssertOnlyAllowedHooksInModule(ModuleDefinition mod, bool expectAchievements)
    {
        foreach (TypeDefinition td in mod.GetTypes())
        {
            string full = td.FullName;
            if (IsInjectedModType(full))
            {
                continue;
            }
            if (full == "ShapeWar.UIStyle")
            {
                continue;
            }
            if (expectAchievements &&
                (full == "ShapeWar.SkillTreeUI" ||
                 full == "ShapeWar.MainMenuUI" ||
                 full == "ShapeWar.ModeSelectUI"))
            {
                continue;
            }

            foreach (MethodDefinition md in td.Methods)
            {
                if (!md.HasBody)
                {
                    continue;
                }
                foreach (Instruction ins in md.Body.Instructions)
                {
                    MemberReference mem = ins.Operand as MemberReference;
                    if (mem != null && mem.DeclaringType != null)
                    {
                        string decl = mem.DeclaringType.FullName;
                        if (IsInjectedModType(decl))
                        {
                            throw new InvalidOperationException(
                                "Unexpected mod reference in " + md.FullName + ": " + mem.FullName);
                        }
                    }
                }
            }
        }
    }
}
