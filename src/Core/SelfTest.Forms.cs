using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

static partial class SelfTest
{
    // The standard Reactive form pattern: client logic marks an input invalid, then reads the form's Valid.
    const string FormSpec = """
        { "screens": [{ "name": "FormCheck",
            "locals": [{ "name": "Name", "type": "Text" }, { "name": "Email", "type": "Email" }],
            "actions": [{ "name": "Save", "flow": [
              { "kind": "if", "name": "HasAt", "condition": "Index(Email, \"@\") > 0", "true": "FormOk", "false": "MarkEmail" },
              { "kind": "assign", "name": "MarkEmail", "assign": { "EmailInput.Valid": "False", "EmailInput.ValidationMessage": "\"Enter a valid email\"" }, "next": "FormOk" },
              { "kind": "if", "name": "FormOk", "condition": "CheckForm.Valid", "true": "Done", "false": "End" },
              { "kind": "message", "name": "Done", "message": "\"Saved\"" } ] }],
            "widgets": [{ "type": "Form", "name": "CheckForm", "children": [
              { "type": "Input", "name": "NameInput", "variable": "Name", "mandatory": "True" },
              { "type": "Input", "name": "EmailInput", "variable": "Email", "inputType": "Email" },
              { "type": "Button", "name": "SaveButton", "onClick": "Save", "children": [{ "type": "Text", "text": "Save" }] } ] }] }] }
        """;

    static void FormValidationTest(string blank, string dir, List<string> failures)
    {
        try
        {
            var spec = Path.Combine(dir, "form.json");
            var output = Path.Combine(dir, "form.oml");
            File.WriteAllText(spec, FormSpec);
            var (code, report) = Apply.Execute(blank, spec, output);
            if (code != 0) { failures.Add("form validation: " + JsonSerializer.Serialize(report)); return; }
            using (var built = Model.Load(output))
            {
                var screen = ElementPaths.Resolve(built, "MainFlow/FormCheck");
                var assign = Descendants(screen).OfType<OutSystems.Model.Logic.Nodes.IAssignNode>().Single(n => n.Label == "MarkEmail");
                var targets = assign.Assignments.Select(a => a.Variable.Text).ToArray();
                if (!targets.SequenceEqual(["EmailInput.Valid", "EmailInput.ValidationMessage"]))
                    failures.Add("form validation: input Valid/ValidationMessage assignments did not round-trip: " + string.Join(", ", targets));
                var button = Descendants(screen).Single(o => Model.NameOf(o) == "SaveButton");
                var validations = Reflect.Get(Reflect.Get(button, "OnClick")!, "BuiltInValidations")?.ToString();
                if (validations != "ValidateAndContinue")
                    failures.Add($"form validation: a Button's On Click Built-in Validations is '{validations}', expected ValidateAndContinue (Yes)");
            }
            // replaceWidgets relies on this: an expression finds a new widget by name, so MarkEmail keeps working when
            // EmailInput is replaced by a new EmailInput, and the edit is refused when no widget of that name stays.
            var replace = Path.Combine(dir, "form-replace.json");
            File.WriteAllText(replace, """
                { "edits": [{ "op": "replaceWidgets", "at": "FormCheck/CheckForm", "widgets": [
                  { "type": "Input", "name": "EmailInput", "variable": "Email", "inputType": "Email" },
                  { "type": "Button", "name": "SaveButton", "onClick": "Save", "children": [{ "type": "Text", "text": "Save" }] } ] }] }
                """);
            var (replaceCode, replaceReport) = Apply.Execute(output, replace, Path.Combine(dir, "form-replaced.oml"));
            if (replaceCode != 0) failures.Add("form validation: replaceWidgets keeping EmailInput failed: " + JsonSerializer.Serialize(replaceReport));
            else using (var replaced = Model.Load(Path.Combine(dir, "form-replaced.oml")))
            {
                var screen = ElementPaths.Resolve(replaced, "MainFlow/FormCheck");
                if (Descendants(screen).Any(o => Model.NameOf(o) == "NameInput") || Model.Errors(replaced).Length > 0)
                    failures.Add("form validation: replaceWidgets left an old widget or a model error");
            }
            File.WriteAllText(replace, """{ "edits": [{ "op": "replaceWidgets", "at": "FormCheck/CheckForm", "widgets": [{ "type": "Text", "text": "Gone" }] }] }""");
            if (!JsonSerializer.Serialize(Apply.Execute(output, replace, Path.Combine(dir, "form-broken.oml")).report).Contains("EmailInput (used by MainFlow/FormCheck/Save/MarkEmail)"))
                failures.Add("form validation: replaceWidgets removing a used widget was not refused with its usage");
            // A Label's target widget is a reference, not an expression: "targetWidget" sets it by name, and replaceWidgets
            // refuses to remove its target even when a new widget gets the same name.
            var labelSpec = Path.Combine(dir, "form-label.json");
            File.WriteAllText(labelSpec, """
                { "screens": [{ "name": "LabelCheck", "locals": [{ "name": "Name", "type": "Text" }], "widgets": [
                  { "type": "Container", "name": "Box", "children": [{ "type": "Input", "name": "NameInput", "variable": "Name" }] },
                  { "type": "Label", "name": "Lbl", "targetWidget": "NameInput", "children": [{ "type": "Text", "text": "Name" }] }] }] }
                """);
            var labelled = Path.Combine(dir, "form-label.oml");
            if (Apply.Execute(blank, labelSpec, labelled).code != 0) failures.Add("form validation: a Label with targetWidget did not save");
            else
            {
                using (var withLabel = Model.Load(labelled))
                    if (Reflect.Get(ElementPaths.Resolve(withLabel, "MainFlow/LabelCheck/Lbl"), "TargetWidget") is not { } target || Model.NameOf(target) != "NameInput")
                        failures.Add("form validation: targetWidget did not set the Label's target widget");
                File.WriteAllText(replace, """{ "edits": [{ "op": "replaceWidgets", "at": "LabelCheck/Box", "widgets": [{ "type": "Input", "name": "NameInput", "variable": "Name" }] }] }""");
                if (!JsonSerializer.Serialize(Apply.Execute(labelled, replace, Path.Combine(dir, "form-label-broken.oml")).report).Contains("NameInput is the target of MainFlow/LabelCheck/Lbl"))
                    failures.Add("form validation: replaceWidgets removing a Label's target was not refused");
            }
            // A listed existing screen takes a new title and styleSheet (UI, rule 3); both must be stored.
            File.WriteAllText(replace, """{ "screens": [{ "name": "FormCheck", "title": "\"Check\"", "styleSheet": ".check { color: red; }" }] }""");
            var (titleCode, titleReport) = Apply.Execute(output, replace, Path.Combine(dir, "form-titled.oml"));
            if (titleCode != 0) failures.Add("form validation: title and styleSheet on an existing screen: " + JsonSerializer.Serialize(titleReport));
            else using (var titled = Model.Load(Path.Combine(dir, "form-titled.oml")))
            {
                var screen = ElementPaths.Resolve(titled, "MainFlow/FormCheck");
                if (Preview.Text(Reflect.Get(screen, "Title")) != "\"Check\"" || Reflect.Get(screen, "StyleSheet") as string != ".check { color: red; }")
                    failures.Add("form validation: an existing screen's new title or styleSheet was not stored");
            }
            // The model checks the target: an unknown widget property and the read-only Form.Valid are refused.
            foreach (var target in new[] { "EmailInput.Bogus", "CheckForm.Valid" })
            {
                File.WriteAllText(spec, FormSpec.Replace("\"EmailInput.Valid\"", $"\"{target}\""));
                if (Apply.Execute(blank, spec, Path.Combine(dir, "form-" + target.Replace(".", "-") + ".oml")).code == 0)
                    failures.Add($"form validation: assigning {target} was not refused");
            }
        }
        catch (Exception e) { failures.Add("form validation: " + (e.InnerException ?? e).Message); }
    }
}
