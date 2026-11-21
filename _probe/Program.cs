using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;

const string Dir = @"C:\YMM4";

AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
{
    var name = new AssemblyName(e.Name).Name + ".dll";
    var p = Path.Combine(Dir, name);
    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
};

var asms = new List<Assembly>();
foreach (var f in Directory.GetFiles(Dir, "YukkuriMovieMaker*.dll"))
{
    try { asms.Add(Assembly.LoadFrom(f)); } catch { }
}
foreach (var f in Directory.GetFiles(Path.Combine(Dir, "user", "plugin"), "*.dll", SearchOption.AllDirectories))
{
    try { asms.Add(Assembly.LoadFrom(f)); } catch { }
}

Type? Find(string full)
{
    foreach (var a in asms) { var t = a.GetType(full); if (t != null) return t; }
    return null;
}

IEnumerable<Type> AllTypes(Assembly a)
{
    try { return a.GetTypes(); }
    catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null)!; }
}

void Dump(string full)
{
    var t = Find(full);
    if (t == null)
    {
        // fall back to a name search
        var hits = asms.SelectMany(AllTypes).Where(x => x.Name == full).Take(5).ToList();
        if (hits.Count == 0) { Console.WriteLine($"==== {full} : NOT FOUND"); return; }
        foreach (var h in hits) Dump(h.FullName!);
        return;
    }
    Console.WriteLine($"==== {t.FullName} (in {t.Assembly.GetName().Name}) abstract={t.IsAbstract} iface={t.IsInterface}");
    Console.WriteLine($"     base: {t.BaseType?.FullName}");
    foreach (var i in t.GetInterfaces()) Console.WriteLine($"     iface: {i.FullName}");
    var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    foreach (var m in t.GetMembers(flags))
    {
        switch (m)
        {
            case ConstructorInfo c:
                Console.WriteLine($"     ctor({string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name + (p.HasDefaultValue ? "=" : "")))})");
                break;
            case MethodInfo mi when !mi.IsSpecialName:
                Console.WriteLine($"     {(mi.IsPublic ? "public" : mi.IsFamily ? "protected" : "private")} {mi.ReturnType.Name} {mi.Name}({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}){(mi.IsAbstract ? " [abstract]" : mi.IsVirtual ? " [virtual]" : "")}");
                break;
            case PropertyInfo pi:
                Console.WriteLine($"     prop {pi.PropertyType.Name} {pi.Name}");
                break;
            case FieldInfo fi:
                Console.WriteLine($"     field {fi.FieldType.Name} {fi.Name}");
                break;
        }
    }
    Console.WriteLine();
}

if (args.Length > 0)
{
    foreach (var a in args) Dump(a);
    return;
}

foreach (var n in new[]
{
    "YukkuriMovieMaker.Player.Video.VideoEffectProcessorBase",
    "YukkuriMovieMaker.Player.Video.TimelineItemSourceDescription",
    "YukkuriMovieMaker.Player.Video.MosaicProcessor",
    "YukkuriMovieMaker.Player.Video.IPixelEffectProcessor",
    "YukkuriMovieMaker.Player.Video.Effects.PixelEffectProcessorBase",
})
    Dump(n);

Console.WriteLine("### user plugins:");
foreach (var a in asms.Where(a => a.Location.Contains("plugin", StringComparison.OrdinalIgnoreCase)))
{
    Console.WriteLine($"Plugin Assembly: {a.GetName().Name}");
    foreach (var t in AllTypes(a))
    {
        Console.WriteLine($"  {t.FullName} (base: {t.BaseType?.Name})");
    }
}
