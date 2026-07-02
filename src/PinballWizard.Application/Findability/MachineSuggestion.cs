namespace PinballWizard.Application.Findability;

// A ranked typeahead suggestion returned by the public machine suggest endpoint
// (GET /api/machines/suggest — ADR-0049 phase 3).
//
// One suggestion represents one distinct machine after edition collapse: if the
// index contains six "Medieval Madness" editions they surface as a single entry
// (the top-ranked one). MachineSuggestService performs the collapse.
//
// Wire format (camelCase via JsonSerializerDefaults.Web):
//   { "opdbId": "GYWBZ-MkPrr", "title": "Willy Wonka & The Chocolate Factory",
//     "manufacturer": "Jersey Jack Pinball", "year": 2019 }
// year may be null for OPDB entries that lack a release year.
public sealed record MachineSuggestion(
    string OpdbId,
    string Title,
    string Manufacturer,
    int? Year);
