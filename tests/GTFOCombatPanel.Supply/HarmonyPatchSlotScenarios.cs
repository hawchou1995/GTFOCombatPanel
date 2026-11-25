using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace HawChow.GTFOCombatPanel.SupplyTests;

// The pack bracket's registration: a finalizer belongs in `finalizer:`, never in the transpiler slot.
//
// The recorded Survival run proves this is not theory. `LogOutput_Survival_snapshot_20261122_103448.log`
// L769-781 carries two
//
//   [Error : HarmonyX] Failed to patch void Gear.ResourcePackFirstPerson::ApplyPack():
//   System.Reflection.TargetParameterCountException: Parameter count mismatch.
//      at HarmonyLib.Internal.Patching.ILManipulator.ApplyTranspilers(...)
//
// lines and then `pack application contract: resolved=2/4; humanApply=False, botApply=False, giveHealth=True,
// disinfection=True`. The cause was in `PackApplicationPatches.Patch`:
// `harmony.Patch(target, prefix, null, finalizer)` - and `Harmony.Patch`'s positions are
// `(original, prefix, postfix, transpiler, finalizer[, ilmanipulator])`. The finalizer therefore landed in the
// *transpiler* slot, and HarmonyX refused to write the IL for a `(PackBracket __state, Exception __exception)`
// method that is not a transpiler.
//
// This is a real regression, not a string count: it loads the very `0Harmony.dll` the plugin is built against,
// reads `Harmony.Patch`'s parameter slots out of it, applies the *same* call shapes to methods compiled into this
// test, and requires that
//
//   * the wrong slot fails exactly the way the log says (a parameter-count mismatch while writing the IL), and
//   * the named slot patches, registers a finalizer, registers no transpiler, and really runs - prefix on the way
//     in, finalizer on the way out, the original exception still propagating.
//
// The source shape is asserted as well, because that is where the defect lived and it must stay caught on a
// machine that has no 0Harmony.dll to load.
internal static class HarmonyPatchSlotScenarios
{
    private static int _prefixRan, _finalizerRan, _targetRan;

    // Never inlined, so Harmony can always find a body to rewrite.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int PatchedTarget()
    {
        _targetRan++;
        return 7;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RefusedTarget() => 11;

    // The production shapes: a prefix that takes injected state, and a finalizer that takes that state and the
    // exception and hands the exception back.
    private static void TestPrefix(ref bool __state)
    {
        _prefixRan++;
        __state = true;
    }

    private static Exception TestFinalizer(bool __state, Exception __exception)
    {
        _finalizerRan++;
        return __exception;
    }

    internal static void Run(Action<bool, string> check)
    {
        SourceShape(check);

        string path = HarmonyPath();
        if (path == null)
        {
            Console.WriteLine("HARMONY_SLOT: SKIP the shipped 0Harmony.dll is not on this machine (0HARMONY_DIR or "
                + "GTFO_DIR\\BepInEx\\core); the source shape above still pins the slot");
            return;
        }

        Assembly harmonyAssembly;
        try { harmonyAssembly = Assembly.LoadFrom(path); }
        catch (Exception error)
        {
            Console.WriteLine("HARMONY_SLOT: SKIP could not load " + path + ": " + error.Message);
            return;
        }

        MethodInfo patch = WidestPatch(harmonyAssembly);
        check(patch != null, "the shipped 0Harmony.dll exposes Harmony.Patch with the (original, prefix, postfix, transpiler, finalizer) slots (" + path + ")");
        if (patch == null) return;
        string[] names = new string[patch.GetParameters().Length];
        for (int index = 0; index < names.Length; index++) names[index] = patch.GetParameters()[index].Name;
        string signature = string.Join(", ", names);
        check(names[0] == "original" && names[1] == "prefix" && names[2] == "postfix" && names[3] == "transpiler"
              && names.Length > 4 && names[4] == "finalizer",
            "the shipped 0Harmony's Patch order is original,prefix,postfix,transpiler,finalizer: the third argument is the postfix and the fourth the transpiler (" + signature + ")");

        Type harmonyMethodType = harmonyAssembly.GetType("HarmonyLib.HarmonyMethod", true);
        Type harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony", true);
        ConstructorInfo wrapper = SingleArgumentConstructor(harmonyMethodType);
        check(wrapper != null, "HarmonyMethod can be built around a MethodInfo, which is what the plugin's Patch helper does");
        if (wrapper == null) return;

        object prefix = wrapper.Invoke(new object[] { (MethodInfo)typeof(HarmonyPatchSlotScenarios).GetMethod(nameof(TestPrefix), BindingFlags.NonPublic | BindingFlags.Static) });
        object finalizer = wrapper.Invoke(new object[] { (MethodInfo)typeof(HarmonyPatchSlotScenarios).GetMethod(nameof(TestFinalizer), BindingFlags.NonPublic | BindingFlags.Static) });

        // ---- the slot the recorded run used ------------------------------------------------------------
        // The old call put the finalizer where `transpiler` lives. This is that call, by name, on the shipped DLL.
        object refused = Activator.CreateInstance(harmonyType, "GTFOCombatPanel.SlotRegression.Wrong");
        Exception failure = InvokePatch(patch, refused, typeof(HarmonyPatchSlotScenarios).GetMethod(nameof(RefusedTarget), BindingFlags.NonPublic | BindingFlags.Static),
            new Dictionary<string, object> { { "prefix", prefix }, { "transpiler", finalizer } });
        check(failure != null,
            "a finalizer-shaped method handed to the transpiler slot is refused rather than installed - this is the recorded Survival run's TargetParameterCountException, and a test that only counted finalizers would have passed it");
        check(failure != null && Mentions(failure, "Parameter count mismatch") || Mentions(failure, "TargetParameterCountException") || Mentions(failure, "IL Compile Error"),
            "and it is refused with the same parameter-count / IL-compile wording the game logged (" + (failure != null ? failure.GetType().Name + ": " + failure.Message : "no failure") + ")");
        check(RefusedTarget() == 11 && _finalizerRan == 0,
            "the refused patch leaves the target running its own code: nothing of the finalizer reached the game method");
        check(FinalizerCount(harmonyType, refused, typeof(HarmonyPatchSlotScenarios).GetMethod(nameof(RefusedTarget), BindingFlags.NonPublic | BindingFlags.Static)) == 0,
            "and nothing was registered as a finalizer for it: the bracket never closed, which is exactly the recorded run's humanApply=False/botApply=False - and why a count of zero alone proves nothing");
        check(TranspilerCount(harmonyType, refused, typeof(HarmonyPatchSlotScenarios).GetMethod(nameof(RefusedTarget), BindingFlags.NonPublic | BindingFlags.Static)) >= 0,
            "the registration Harmony kept for the refused target is readable, so the two counts above are Harmony's own answer and not a guess");

        // ---- the named slot the fix uses ---------------------------------------------------------------
        object installed = Activator.CreateInstance(harmonyType, "GTFOCombatPanel.SlotRegression.Named");
        Exception refusal = InvokePatch(patch, installed, typeof(HarmonyPatchSlotScenarios).GetMethod(nameof(PatchedTarget), BindingFlags.NonPublic | BindingFlags.Static),
            new Dictionary<string, object> { { "prefix", prefix }, { "finalizer", finalizer } });
        check(refusal == null, "the same finalizer passed as `finalizer:` installs (" + (refusal != null ? refusal.Message : "no exception") + ")");
        if (refusal != null) return;
        check(TranspilerCount(harmonyType, installed, typeof(HarmonyPatchSlotScenarios).GetMethod(nameof(PatchedTarget), BindingFlags.NonPublic | BindingFlags.Static)) == 0,
            "the installed contract declares no transpiler at all, so `final=0,transpiler=0` here means the finalizer went somewhere else - which is what the count alone cannot tell you");
        check(FinalizerCount(harmonyType, installed, typeof(HarmonyPatchSlotScenarios).GetMethod(nameof(PatchedTarget), BindingFlags.NonPublic | BindingFlags.Static)) == 1,
            "and it is registered as a finalizer: the pack bracket closes on the way out");

        _prefixRan = 0; _finalizerRan = 0; _targetRan = 0;
        int value = PatchedTarget();
        check(value == 7 && _targetRan == 1,
            "the patched method still returns its own result: the bracket wraps the call instead of replacing it");
        check(_prefixRan == 1 && _finalizerRan == 1,
            "the real IL runs the production shapes: the prefix opens the bracket and the finalizer closes it");
    }

    // ---- the source shape, which is where the defect lived ------------------------------------------------
    private static void SourceShape(Action<bool, string> check)
    {
        string root = SourceRoot();
        check(root != null, "the pack registration's source is locatable from this test's output folder");
        if (root == null) return;
        string source = Code(File.ReadAllText(Path.Combine(root, "PackApplicationPatches.cs")));
        int call = source.IndexOf("harmony.Patch(", StringComparison.Ordinal);
        check(call > 0, "the plugin registers a patch through Harmony.Patch");
        check(call > 0 && source.IndexOf("harmony.Patch(", call + 1, StringComparison.Ordinal) < 0,
            "the registration happens in exactly one line of production code, so this shape is the whole of the rule");
        string arguments = Arguments(source, call);
        check(arguments != null, "the registration's argument list is readable as a list of arguments");
        if (arguments == null) return;
        List<string> list = Split(arguments);
        check(list.Count >= 1 && !list[0].Contains(':'),
            "the first argument is the target, positionally, so the call reads like the API it wraps");
        bool named = true;
        for (int index = 1; index < list.Count; index++)
            if (!list[index].Contains(':')) named = false;
        check(named,
            "every argument after the target is NAMED: a positional third argument is the postfix and a positional fourth is the transpiler, which is exactly how the finalizer reached ApplyTranspilers (" + arguments.Trim() + ")");
        check(arguments.Contains("finalizer:"),
            "the pack bracket's finalizer is passed as `finalizer:`, the slot Harmony reads a (state, exception) method from");
        check(!arguments.Contains("transpiler:"),
            "no transpiler is declared for this contract: it has none, and declaring one is what the old call accidentally did");

        int installs = 0, index2 = 0;
        while ((index2 = source.IndexOf("= Patch(harmony,", index2, StringComparison.Ordinal)) >= 0) { installs++; index2 += 14; }
        check(installs == 4,
            "the other three hooks are untouched: the human bracket, the bot bracket, GiveHealth and GiveDisinfection are still installed through the same helper (" + installs + " registrations)");
        check(source.Contains("nameof(HealthGivePrefix)") && source.Contains("nameof(DisinfectionPrefix)"),
            "the two health/disinfection prefixes - the pair that resolved in the recorded run - are still named by the registration");
        check(source.Contains("\" is not in this build\"") && source.Contains("\" could not be patched: \""),
            "a missing target and a refused patch still degrade into one diagnostic line each instead of a failed plugin load");
    }

    // ---- plumbing ----------------------------------------------------------------------------------------

    private static string HarmonyPath()
    {
        var candidates = new List<string>(4);
        foreach (string variable in new[] { "0HARMONY_DIR", "GTFO_DIR" })
        {
            string root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(root)) continue;
            candidates.Add(Path.Combine(root, "BepInEx", "core", "0Harmony.dll"));
        }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "0Harmony.dll"));
        foreach (string candidate in candidates) if (File.Exists(candidate)) return candidate;
        return null;
    }

    // The widest overload whose parameters after the target are all HarmonyMethod: that is the one the compiler
    // binds `Patch(target, prefix: ..., finalizer: ...)` to.
    private static MethodInfo WidestPatch(Assembly harmony)
    {
        MethodInfo best = null;
        foreach (MethodInfo candidate in harmony.GetType("HarmonyLib.Harmony", true).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (candidate.Name != "Patch") continue;
            ParameterInfo[] parameters = candidate.GetParameters();
            if (parameters.Length < 5 || parameters[0].ParameterType != typeof(MethodBase)) continue;
            bool methods = true;
            for (int index = 1; index < parameters.Length; index++)
                if (parameters[index].ParameterType.Name != "HarmonyMethod") methods = false;
            if (!methods) continue;
            if (best == null || parameters.Length > best.GetParameters().Length) best = candidate;
        }
        return best;
    }

    private static ConstructorInfo SingleArgumentConstructor(Type harmonyMethod)
    {
        foreach (ConstructorInfo candidate in harmonyMethod.GetConstructors())
        {
            ParameterInfo[] parameters = candidate.GetParameters();
            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(MethodInfo)) return candidate;
        }
        return null;
    }

    // One Patch call, its arguments bound by *name* - the same binding the production code now gets from the
    // compiler. Returns the exception a refused patch threw, or null when the patch went in.
    private static Exception InvokePatch(MethodInfo patch, object harmony, MethodBase target, Dictionary<string, object> named)
    {
        ParameterInfo[] parameters = patch.GetParameters();
        var arguments = new object[parameters.Length];
        for (int index = 0; index < parameters.Length; index++)
        {
            if (index == 0) { arguments[index] = target; continue; }
            named.TryGetValue(parameters[index].Name, out object value);
            arguments[index] = value;
        }
        try { patch.Invoke(harmony, arguments); return null; }
        catch (TargetInvocationException error) { return error.InnerException ?? error; }
    }

    private static int FinalizerCount(Type harmonyType, object harmony, MethodBase target)
        => Patches(harmonyType, harmony, target, "Finalizers");

    private static int TranspilerCount(Type harmonyType, object harmony, MethodBase target)
        => Patches(harmonyType, harmony, target, "Transpilers");

    private static int Patches(Type harmonyType, object harmony, MethodBase target, string name)
    {
        try
        {
            MethodInfo info = harmonyType.GetMethod("GetPatchInfo", new[] { typeof(MethodBase) });
            object patches = info.Invoke(harmony, new object[] { target });
            if (patches == null) return 0;
            // The shipped HarmonyX keeps these as `ReadOnlyCollection<Patch>` fields; a newer one exposes them as
            // properties. Either way the count comes from the collection Harmony itself would apply.
            Type type = patches.GetType();
            object list = type.GetProperty(name)?.GetValue(patches, null);
            if (list == null)
            {
                FieldInfo field = type.GetField(name);
                if (field != null) list = field.GetValue(patches);
            }
            if (list is System.Collections.ICollection collection) return collection.Count;
            int count = 0;
            if (list is System.Collections.IEnumerable items) foreach (object ignored in items) count++;
            return count;
        }
        catch { return -1; }
    }

    private static bool Mentions(Exception value, string text)
    {
        for (Exception error = value; error != null; error = error.InnerException)
            if (error.GetType().Name.Contains(text) || (error.Message != null && error.Message.Contains(text))) return true;
        return false;
    }

    // The text between the parentheses of the call that starts at `start`, with comments already stripped.
    private static string Arguments(string source, int start)
    {
        int open = source.IndexOf('(', start);
        if (open < 0) return null;
        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            char character = source[index];
            if (character == '(') depth++;
            else if (character == ')')
            {
                depth--;
                if (depth == 0) return source.Substring(open + 1, index - open - 1);
            }
        }
        return null;
    }

    // Top-level commas only: a ternary inside an argument must not split it.
    private static List<string> Split(string arguments)
    {
        var parts = new List<string>(6);
        int depth = 0;
        var current = new System.Text.StringBuilder();
        foreach (char character in arguments)
        {
            if (character == '(' || character == '[' || character == '<') depth++;
            else if (character == ')' || character == ']' || character == '>') depth--;
            if (character == ',' && depth == 0) { parts.Add(current.ToString()); current.Clear(); continue; }
            current.Append(character);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    private static string Code(string text)
    {
        if (text == null) return string.Empty;
        var kept = new List<string>(text.Length / 40 + 8);
        foreach (string line in text.Split('\n'))
            if (!line.TrimStart().StartsWith("//", StringComparison.Ordinal)) kept.Add(line);
        return string.Join("\n", kept);
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GTFOCombatPanel.csproj"))) directory = directory.Parent;
        return directory != null ? directory.FullName : null;
    }
}
