using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

// Entity options and indexes, aggregate options, group-by aliases and group-level parts, and the aggregate edit ops:
// each is applied, must save with no new error or warning, and must read back through describe after reopening.
static partial class SelfTest
{
    const string DataSpec = """
        {
          "onException": { "name": "OnException", "flow": "MainFlow", "nodes": [
            { "kind": "exceptionHandler", "name": "OnAllErrors", "exception": "AllExceptions", "logError": true } ] },
          "globalExceptionHandler": "MainFlow/OnException",
          "entities": [
            { "name": "Country", "identifier": "Code", "attributes": [ { "name": "Code", "length": 2 }, { "name": "Name", "length": 100 } ],
              "indexes": [ { "attributes": ["Name"], "unique": true } ] },
            { "name": "Person", "labelAttribute": "Name", "orderByAttribute": "Name", "isActiveAttribute": "Active",
              "exposeReadOnly": false, "updateBehavior": "changedAttributes",
              "attributes": [ { "name": "Name" }, { "name": "Active", "type": "Boolean" }, { "name": "BirthDate", "type": "Date" },
                { "name": "Salary", "type": "Decimal" }, { "name": "CountryId", "type": "Country Identifier" } ],
              "indexes": [ { "name": "IX_Person_CountryName", "attributes": ["CountryId", "Name"] } ] },
            { "name": "Ticket", "autoNumber": false, "attributes": [ { "name": "Title" } ] }
          ],
          "serverActions": [ { "name": "PersonStats", "public": true, "inputs": [ { "name": "SortBy", "type": "Text" } ],
            "outputs": [ { "name": "Count", "type": "Integer" } ], "flow": [
              { "kind": "aggregate", "name": "ByYear", "sources": ["Person"], "cacheInMinutes": 5, "timeout": 30,
                "groupBy": [ { "attribute": "Year(Person.BirthDate)", "as": "BirthYear" }, "Month(Person.BirthDate)", "Person.CountryId" ],
                "aggregates": [ { "name": "People", "function": "Count", "attribute": "Person.Id" }, { "name": "Payroll", "function": "Sum", "attribute": "Person.Salary" } ],
                "groupFilters": [ "People > 1" ], "groupSorts": [ { "attribute": "BirthYear", "descending": true } ],
                "groupCalculated": [ { "name": "Average", "expression": "Payroll / People" } ] },
              { "kind": "aggregate", "name": "Sorted", "sources": ["Person"], "sorts": [ { "attribute": "SortBy", "dynamic": true } ], "maxRecords": 10 },
              { "kind": "assign", "name": "Out", "assign": { "Count": "ByYear.List.Length + Sorted.List.Length" } } ] },
            { "name": "RunStats", "public": true, "flow": [ { "kind": "execute", "name": "Run", "action": "PersonStats", "args": { "SortBy": "\"Person.Name\"" } } ] } ],
          "screens": [ { "name": "People", "locals": [ { "name": "SelectedId", "type": "Country Identifier" } ],
            "aggregates": [ { "name": "GetCountries", "sources": ["Country"] },
              { "name": "GetPeople", "fetch": "onDemand", "timeout": 20, "onAfterFetch": "Fetched", "sources": ["Person"], "filters": ["Person.CountryId = SelectedId"] } ],
            "dataActions": [ { "name": "LoadTotal", "fetch": "onDemand", "timeout": 15, "onAfterFetch": "Fetched", "outputs": [ { "name": "Total", "type": "Integer" } ],
              "flow": [ { "kind": "assign", "assign": { "Total": "1" } } ] } ],
            "actions": [ { "name": "Fetched", "flow": [] }, { "name": "Pick", "inputs": [ { "name": "Code", "type": "Country Identifier" } ], "flow": [
              { "kind": "assign", "name": "Remember", "assign": { "SelectedId": "Code" } },
              { "kind": "refreshData", "name": "LoadPeople", "dataSource": "GetPeople" },
              { "kind": "refreshData", "name": "LoadTotal", "dataSource": "LoadTotal" } ] } ],
            "widgets": [
              { "type": "TableRecords", "source": "GetCountries.List", "row": [ { "type": "RowCell", "children": [
                { "type": "Button", "onClick": "Pick", "args": { "Code": "GetCountries.List.Current.Country.Code" },
                  "children": [ { "type": "Expression", "value": "GetCountries.List.Current.Country.Name" } ] } ] } ] },
              { "type": "TableRecords", "source": "GetPeople.List", "row": [ { "type": "RowCell", "children": [
                { "type": "Expression", "value": "GetPeople.List.Current.Person.Name" } ] } ] },
              { "type": "Expression", "value": "LoadTotal.Total" } ] } ]
        }
        """;

    // Additive changes on the entities and aggregate edits built above: allowed on existing elements.
    const string DataEditSpec = """
        {
          "entities": [ { "name": "Person", "attributes": [ { "name": "Email", "type": "Email" } ],
            "indexes": [ { "attributes": ["Email"], "unique": true } ] } ],
          "edits": [
            { "op": "addToAggregate", "aggregate": "PersonStats/Sorted", "sources": ["Country"],
              "joins": [ { "type": "with or without", "left": "Person", "right": "Country", "condition": "Person.CountryId = Country.Code" } ],
              "filters": [ "Person.Salary > 0" ], "sorts": [ { "attribute": "Country.Name" } ],
              "calculated": [ { "name": "Label", "expression": "Person.Name + \" \" + Country.Name" } ] },
            { "op": "set", "element": "PersonStats/ByYear/CombineSources1/Filter[0]", "properties": { "condition": "People > 2" } },
            { "op": "setArgs", "element": "RunStats/Run", "args": { "SortBy": "\"Person.Salary DESC\"" } }
          ]
        }
        """;

    // Parts added by addToAggregate exist only after its expressions ran, so they are changed in a second apply.
    const string DataChangeSpec = """
        { "edits": [
            { "op": "set", "element": "PersonStats/Sorted/Persons/Filter[0]", "properties": { "condition": "Person.Salary >= 0" } },
            { "op": "set", "element": "PersonStats/Sorted/Persons/JoinCondition[0]", "properties": { "joinType": "Inner" } },
            { "op": "set", "element": "PersonStats/Sorted/Persons/AttributeSort[1]", "properties": { "sortDirection": "Descending" } },
            { "op": "delete", "element": "PersonStats/Sorted/Persons/Label" } ] }
        """;

    static void DataTest(string blank, string dir, List<string> failures)
    {
        // expected: ids of oml's own new warnings that this step must report (and may).
        string? Built(string name, string source, string spec, params string[] expected)
        {
            var path = Path.Combine(dir, name + ".json");
            var output = Path.Combine(dir, name + ".oml");
            File.WriteAllText(path, spec);
            var (code, report) = Apply.Execute(source, path, output);
            var json = JsonSerializer.SerializeToElement(report);
            var ids = json.TryGetProperty("newWarnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array
                ? warnings.EnumerateArray().Select(w => w.GetProperty("id").GetString()).ToList() : new List<string?>();
            if (code != 0 || !ids.Order().SequenceEqual(expected.Order()))
            { failures.Add($"data {name}: " + JsonSerializer.Serialize(report)); return null; }
            return output;
        }
        void Expect(string module, string element, params string[] lines)
        {
            using var m = Model.Load(module);
            var text = JsonSerializer.SerializeToElement(Describe.Run(m, element, 0, [])).GetProperty("text").GetString()!;
            foreach (var line in lines)
                if (!text.Contains(line, StringComparison.Ordinal)) failures.Add($"data describe {element} missing: {line}");
        }
        void Refused(string name, string source, string spec, string problem)
        {
            var path = Path.Combine(dir, name + ".json");
            var output = Path.Combine(dir, name + ".oml");
            File.WriteAllText(path, spec);
            var (code, report) = Apply.Execute(source, path, output);
            var text = JsonSerializer.Serialize(report, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            if (code == 0 || File.Exists(output) || !text.Contains(problem, StringComparison.Ordinal)) failures.Add($"data {name} not refused with '{problem}': {text}");
        }

        if (Built("data", blank, DataSpec, "PublicServerAction") is not { } built) return;
        Expect(built, "Country", "identifier Code", "index IX_Name(Name) [unique]");
        Expect(built, "Person", "label attribute Name", "order by attribute Name", "is active attribute Active",
            "expose read only: no", "update behavior: changed attributes", "index IX_Person_CountryName(CountryId, Name)");
        Expect(built, "Ticket", "identifier Id");
        Expect(built, "PersonStats",
            "group by BirthYear = Year(Person.BirthDate), MonthBirthDate = Month(Person.BirthDate), Person.CountryId",
            "having People > 1 calculate grouped Average = Payroll / People order groups by BirthYear desc cache 5 min timeout 30 s",
            "query Sorted: Person order by SortBy [dynamic] max 10");
        Expect(built, "MainFlow/People", "[on demand] timeout 20 s on after fetch -> Fetched", "DataAction LoadTotal() -> Total: Integer [on demand] timeout 15 s on after fetch -> Fetched");

        if (Built("data-edit", built, DataEditSpec, "UniqueIndexOnExistingEntity") is { } edited)
        {
            Expect(edited, "Person", "index IX_Email(Email) [unique]");
            Expect(edited, "PersonStats", "having People > 2", "calculate Label = Person.Name");
            Expect(edited, "RunStats", "SortBy = \"Person.Salary DESC\"");
            if (Built("data-change", edited, DataChangeSpec) is { } changed)
                Expect(changed, "PersonStats", "query Sorted: Person only with Country on Person.CountryId = Country.Code where Person.Salary >= 0 order by SortBy [dynamic], Country.Name desc max 10");
        }

        // Changing how existing rows are keyed, read or written is refused, through entity keys and through set.
        Refused("data-identifier", built, """{ "entities": [ { "name": "Person", "identifier": "Name" } ] }""",
            "refused: 'Person' is an existing entity. oml never changes its identifier");
        Refused("data-active", built, """{ "edits": [ { "op": "set", "element": "Person", "properties": { "isActiveAttribute": null } } ] }""",
            "oml never changes its is active attribute");
        Refused("data-index", built, """{ "entities": [ { "name": "Person", "indexes": [ { "attributes": ["Missing"] } ] } ] }""",
            "'Missing' is not an attribute of Person");
        // Deleting a filter, sort or group of a grouped aggregate can drop its source; oml refuses such a delete.
        // On 11.55.93 this one drops it. A build that keeps the source may save it: then the sort must be gone and the source kept.
        var groupDelete = Path.Combine(dir, "data-group-delete.json");
        File.WriteAllText(groupDelete, """{ "edits": [ { "op": "delete", "element": "PersonStats/ByYear/CombineSources1/AttributeSort[0]" } ] }""");
        var (deleteCode, deleteReport) = Apply.Execute(built, groupDelete, Path.Combine(dir, "data-group-delete.oml"));
        if (deleteCode == 0)
        {
            Expect(Path.Combine(dir, "data-group-delete.oml"), "PersonStats", "query ByYear: Person group by");
            using var m = Model.Load(Path.Combine(dir, "data-group-delete.oml"));
            if (JsonSerializer.SerializeToElement(Describe.Run(m, "PersonStats", 0, [])).GetProperty("text").GetString()!.Contains("order groups by BirthYear"))
                failures.Add("data group-level delete saved but the group sort is still there");
        }
        else if (!JsonSerializer.Serialize(deleteReport).Contains("(a model side effect)", StringComparison.Ordinal))
            failures.Add("data group-level delete: " + JsonSerializer.Serialize(deleteReport));
    }
}
