// Converts the GeoNames US postal-code file into a Lookup dataset.
//
// Usage:
//   dotnet run --project tools/ZipConverter -- US.txt zipcodes.json [version-label]
//
// Input: the tab-separated US.txt from https://download.geonames.org/export/zip/US.zip
// (columns: country, postal code, place name, state name, state abbrev, county, ...).
// Output: a Lookup dataset JSON — one item per ZIP:
//   { "code": "90210", "title": "Beverly Hills, CA",
//     "category": "Los Angeles, California" }
// State and county live in `category`, so they are searchable (category feeds both
// the phrase tier and the whole-word index) and show in the result subtitle.

using System.Text.Encodings.Web;
using System.Text.Json;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: dotnet run --project tools/ZipConverter -- <US.txt> <zipcodes.json> [version-label]");
    return 1;
}

var src = args[0];
var dest = args[1];
var version = args.Length > 2 ? args[2] : "geonames";

var items = new List<(string Code, string Title, string Category)>();
var seen = new HashSet<string>(StringComparer.Ordinal);

foreach (var line in File.ReadLines(src))
{
    var cols = line.Split('\t');
    if (cols.Length < 6) continue;

    var zip = cols[1].Trim();
    var place = cols[2].Trim();
    var stateName = cols[3].Trim();
    var stateAbbr = cols[4].Trim();
    var county = cols[5].Trim();

    // First entry per ZIP wins (duplicates are rare place-name aliases).
    if (zip.Length == 0 || place.Length == 0 || !seen.Add(zip)) continue;

    var title = stateAbbr.Length > 0 ? $"{place}, {stateAbbr}" : place;
    var category = string.Join(", ", new[] { county, stateName }.Where(p => p.Length > 0));
    items.Add((zip, title, category));
}

items.Sort((a, b) => string.CompareOrdinal(a.Code, b.Code));

using (var stream = File.Create(dest))
using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions
{
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep ñ/ü readable
}))
{
    json.WriteStartObject();
    json.WriteString("dataset", "zipcodes");
    json.WriteString("version", version);
    json.WriteStartArray("items");
    foreach (var (code, title, category) in items)
    {
        json.WriteStartObject();
        json.WriteString("code", code);
        json.WriteString("title", title);
        if (category.Length > 0) json.WriteString("category", category);
        json.WriteEndObject();
    }
    json.WriteEndArray();
    json.WriteEndObject();
}

Console.WriteLine($"{items.Count} ZIP codes -> {dest}");
return 0;
