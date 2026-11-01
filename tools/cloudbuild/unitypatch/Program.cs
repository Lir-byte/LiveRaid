using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Linq;

// Adds the Unity 2022.3 physics query API (QueryParameters + RaycastCommand ctor) to a 2021.3 reference assembly.
class P
{
    static void Main(string[] a)
    {
        var rp = new DefaultAssemblyResolver();
        rp.AddSearchDirectory(System.IO.Path.GetDirectoryName(a[0]));
        var asm = AssemblyDefinition.ReadAssembly(a[0], new ReaderParameters { AssemblyResolver = rp });
        var m = asm.MainModule;
        if (m.Types.Any(t => t.FullName == "UnityEngine.QueryParameters")) { Console.WriteLine("already patched"); return; }
        var core = m.AssemblyReferences.First(r => r.Name == "UnityEngine.CoreModule");
        var vec3 = new TypeReference("UnityEngine", "Vector3", m, core, true);
        var qti = m.Types.First(t => t.FullName == "UnityEngine.QueryTriggerInteraction");
        var valueType = m.Types.First(t => t.FullName == "UnityEngine.RaycastHit").BaseType;
        var qp = new TypeDefinition("UnityEngine", "QueryParameters",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout | TypeAttributes.BeforeFieldInit, valueType);
        m.Types.Add(qp);
        qp.Fields.Add(new FieldDefinition("layerMask", FieldAttributes.Public, m.TypeSystem.Int32));
        qp.Fields.Add(new FieldDefinition("hitMultipleFaces", FieldAttributes.Public, m.TypeSystem.Boolean));
        qp.Fields.Add(new FieldDefinition("hitTriggers", FieldAttributes.Public, qti));
        qp.Fields.Add(new FieldDefinition("hitBackfaces", FieldAttributes.Public, m.TypeSystem.Boolean));
        var ctor = Ctor(m, qp);
        AddParam(ctor, "layerMask", m.TypeSystem.Int32, -5);
        AddParam(ctor, "hitMultipleFaces", m.TypeSystem.Boolean, false);
        AddParam(ctor, "hitTriggers", qti, 0);
        AddParam(ctor, "hitBackfaces", m.TypeSystem.Boolean, false);
        var getDef = new MethodDefinition("get_Default", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.HideBySig, qp);
        Throw(m, getDef);
        qp.Methods.Add(getDef);
        var prop = new PropertyDefinition("Default", PropertyAttributes.None, qp) { GetMethod = getDef };
        qp.Properties.Add(prop);

        foreach (var name in new[] { "UnityEngine.RaycastCommand" })
        {
            var rc = m.Types.First(t => t.FullName == name);
            var c = Ctor(m, rc);
            AddParam(c, "from", vec3, null);
            AddParam(c, "direction", vec3, null);
            AddParam(c, "queryParameters", qp, null);
            AddParam(c, "distance", m.TypeSystem.Single, float.MaxValue);
            var c2 = Ctor(m, rc);
            AddParam(c2, "physicsScene", new TypeReference("UnityEngine", "PhysicsScene", m, m, true) { }, null);
            c2.Parameters[0].ParameterType = m.Types.First(t => t.FullName == "UnityEngine.PhysicsScene");
            AddParam(c2, "from", vec3, null);
            AddParam(c2, "direction", vec3, null);
            AddParam(c2, "queryParameters", qp, null);
            AddParam(c2, "distance", m.TypeSystem.Single, float.MaxValue);
            var qf = new PropertyDefinition("queryParameters", PropertyAttributes.None, qp);
            var g = new MethodDefinition("get_queryParameters", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, qp);
            Throw(m, g); rc.Methods.Add(g); qf.GetMethod = g;
            var s = new MethodDefinition("set_queryParameters", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, m.TypeSystem.Void);
            s.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, qp));
            Throw(m, s); rc.Methods.Add(s); qf.SetMethod = s;
            rc.Properties.Add(qf);
        }
        asm.Write(a[1]);
        Console.WriteLine("patched -> " + a[1]);
    }

    static MethodDefinition Ctor(ModuleDefinition m, TypeDefinition t)
    {
        var c = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, m.TypeSystem.Void);
        Throw(m, c);
        t.Methods.Add(c);
        return c;
    }

    static void AddParam(MethodDefinition md, string n, TypeReference t, object def)
    {
        var p = new ParameterDefinition(n, def == null ? ParameterAttributes.None : ParameterAttributes.Optional | ParameterAttributes.HasDefault, t);
        if (def != null) p.Constant = def;
        md.Parameters.Add(p);
    }

    static void Throw(ModuleDefinition m, MethodDefinition md)
    {
        var il = md.Body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldnull));
        il.Append(il.Create(OpCodes.Throw));
    }
}
