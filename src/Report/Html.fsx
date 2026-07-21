(**
    Self-contained HTML report.

    Everything is inlined: styles, script, and chart geometry. The file opens
    correctly with no network access, which matters both for offline use and
    because a scan report lists the paths on someone's disk — it should not
    make a request to a third party when opened.

    All interpolated text is escaped. A filename may legitimately contain
    `<` or `&`.
*)
module Report.Html

open System
open System.Text

/// Escapes text for safe interpolation into HTML.
let private escape (text: string) : string =
    text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")

/// A fixed palette, chosen to stay legible in both light and dark themes.
let private palette =
    [| "#4f8ef7"; "#f7934f"; "#4fd18b"; "#d14f8e"; "#a24ff7"
       "#f7d64f"; "#4fd1d1"; "#f76a4f"; "#8ef74f"; "#9e9e9e" |]

/// Renders an SVG donut chart of category shares.
let private donut (slices: (string * int64) list) : string =
    let total = slices |> List.sumBy snd
    if total = 0L then "<p>No data.</p>"
    else
        let sb = StringBuilder()
        sb.Append "<svg viewBox=\"0 0 42 42\" class=\"donut\" role=\"img\">" |> ignore
        // A circle of circumference 100 lets each slice be expressed directly
        // as a percentage via stroke-dasharray, with no trigonometry.
        let mutable offset = 25.0
        slices |> List.iteri (fun i (_, value) ->
            let percent = float value / float total * 100.0
            let color = palette.[i % palette.Length]
            sb.Append(
                sprintf "<circle cx=\"21\" cy=\"21\" r=\"15.915\" fill=\"transparent\" stroke=\"%s\" stroke-width=\"6\" stroke-dasharray=\"%.2f %.2f\" stroke-dashoffset=\"%.2f\"/>"
                    color percent (100.0 - percent) offset) |> ignore
            offset <- offset - percent)
        sb.Append "</svg>" |> ignore
        sb.ToString()

/// Renders a simple treemap of the largest items as proportional bars.
///
/// A full squarified treemap adds considerable geometry for little gain at
/// this scale, so rows are laid out proportionally by width — the comparison
/// a reader actually makes is between sizes, which this preserves.
let private treemap (items: (string * int64) list) : string =
    let total = items |> List.sumBy snd
    if total = 0L then ""
    else
        let sb = StringBuilder()
        sb.Append "<div class=\"treemap\">" |> ignore
        items |> List.iteri (fun i (label, value) ->
            let percent = float value / float total * 100.0
            let color = palette.[i % palette.Length]
            sb.Append(
                sprintf "<div class=\"tile\" style=\"width:%.2f%%;background:%s\" title=\"%s — %s\"><span>%s</span></div>"
                    (max percent 2.0) color (escape label) (Utilities.Format.bytes value) (escape label)) |> ignore)
        sb.Append "</div>" |> ignore
        sb.ToString()

/// Renders the complete HTML document.
let render (result: Scanner.ScanResult) : string =
    let grouped = Scanner.byCategory result
    let categoryTotals =
        grouped |> List.map (fun (c, fs) -> Types.Category.toText c, fs |> List.sumBy (fun f -> f.Meta.SizeBytes))
    let largest =
        result.Findings |> List.sortByDescending (fun f -> f.Meta.SizeBytes) |> List.truncate 200

    let rows =
        largest
        |> List.map (fun f ->
            sprintf "<tr data-risk=\"%s\"><td><span class=\"risk r-%s\">%s</span></td><td class=\"num\" data-bytes=\"%d\">%s</td><td class=\"path\">%s</td><td>%s</td></tr>"
                (Types.RiskLevel.toText f.Risk)
                ((Types.RiskLevel.toText f.Risk).ToLowerInvariant())
                (Types.RiskLevel.toText f.Risk)
                f.Meta.SizeBytes
                (Utilities.Format.bytes f.Meta.SizeBytes)
                (escape f.Meta.Path)
                (escape f.RuleName))
        |> String.concat "\n"

    let categoryRows =
        grouped
        |> List.map (fun (category, findings) ->
            let total = findings |> List.sumBy (fun f -> f.Meta.SizeBytes)
            let claimable =
                findings |> List.filter (fun f -> f.Candidate.IsSome) |> List.sumBy (fun f -> f.Meta.SizeBytes)
            sprintf "<tr><td>%s</td><td class=\"num\">%s</td><td class=\"num\">%s</td><td class=\"num\">%d</td></tr>"
                (escape (Types.Category.toText category))
                (Utilities.Format.bytes total)
                (Utilities.Format.bytes claimable)
                findings.Length)
        |> String.concat "\n"

    let unreadableNote =
        let n = Scanner.unreadableDirectoryCount result
        if n = 0 then ""
        else sprintf "<p class=\"warn\">%d directories were unreadable. Grant Full Disk Access to your terminal to include them.</p>" n

    // Assembled as one string. No external resource is referenced anywhere.
    sprintf """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>DiskCleanupAdvisor report</title>
<style>
:root { --bg:#ffffff; --fg:#1a1a1a; --muted:#666; --line:#e3e3e3; --card:#f7f7f8; }
@media (prefers-color-scheme: dark) {
  :root { --bg:#14161a; --fg:#e8e8ea; --muted:#9aa0a6; --line:#2a2d33; --card:#1b1e24; }
}
* { box-sizing:border-box; }
body { margin:0; padding:2rem 1.25rem; background:var(--bg); color:var(--fg);
       font:15px/1.55 ui-sans-serif,-apple-system,Segoe UI,Roboto,sans-serif; }
main { max-width:1100px; margin:0 auto; }
h1 { font-size:1.6rem; margin:0 0 .25rem; }
.sub { color:var(--muted); margin:0 0 2rem; }
.cards { display:grid; grid-template-columns:repeat(auto-fit,minmax(220px,1fr)); gap:1rem; margin-bottom:2rem; }
.card { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:1rem 1.1rem; }
.card .label { color:var(--muted); font-size:.82rem; text-transform:uppercase; letter-spacing:.04em; }
.card .value { font-size:1.5rem; font-weight:600; margin-top:.3rem; }
.donut { width:180px; height:180px; }
.treemap { display:flex; height:46px; border-radius:8px; overflow:hidden; margin:1rem 0 2rem; }
.tile { display:flex; align-items:center; justify-content:center; overflow:hidden; }
.tile span { font-size:.7rem; color:#fff; white-space:nowrap; padding:0 .3rem; }
table { width:100%%; border-collapse:collapse; margin-bottom:2rem; }
th, td { text-align:left; padding:.5rem .6rem; border-bottom:1px solid var(--line); }
th { color:var(--muted); font-size:.8rem; text-transform:uppercase; letter-spacing:.04em; cursor:pointer; user-select:none; }
td.num { text-align:right; font-variant-numeric:tabular-nums; }
td.path { font-family:ui-monospace,SFMono-Regular,Menlo,monospace; font-size:.82rem; word-break:break-all; }
.risk { font-size:.72rem; padding:.15rem .45rem; border-radius:4px; font-weight:600; }
.r-safe { background:#1f7a4d; color:#fff; }
.r-probablysafe { background:#2b6cb0; color:#fff; }
.r-review { background:#9a6a00; color:#fff; }
.r-dangerous { background:#8a2be2; color:#fff; }
.r-neverdelete { background:#a02020; color:#fff; }
#q { width:100%%; padding:.6rem .7rem; margin-bottom:1rem; border:1px solid var(--line);
     border-radius:8px; background:var(--card); color:var(--fg); font-size:.9rem; }
.warn { background:#9a6a0022; border-left:3px solid #9a6a00; padding:.7rem .9rem; border-radius:4px; }
details { margin-bottom:1.5rem; }
summary { cursor:pointer; font-weight:600; padding:.4rem 0; }
</style>
</head>
<body>
<main>
<h1>DiskCleanupAdvisor</h1>
<p class="sub">Generated %s &middot; %A &middot; %d items across %d roots</p>

<div class="cards">
  <div class="card"><div class="label">Reclaimable</div><div class="value">%s</div>
    <div class="sub" style="margin:0">eligible for automatic removal</div></div>
  <div class="card"><div class="label">Pending review</div><div class="value">%s</div>
    <div class="sub" style="margin:0">requires your judgment</div></div>
  <div class="card"><div class="label">Scan duration</div><div class="value">%s</div></div>
</div>

%s

<details open><summary>By category</summary>
%s
%s
<table>
<thead><tr><th>Category</th><th class="num">Total</th><th class="num">Reclaimable</th><th class="num">Items</th></tr></thead>
<tbody>
%s
</tbody></table>
</details>

<details open><summary>Largest items</summary>
<input id="q" type="search" placeholder="Filter by path or rule&hellip;" aria-label="Filter items">
<table id="items">
<thead><tr><th>Risk</th><th class="num" data-sort="num">Size</th><th>Path</th><th>Rule</th></tr></thead>
<tbody>
%s
</tbody></table>
</details>

<p class="sub">Removal moves items to the Trash. Space is reclaimed when you empty it.</p>
</main>
<script>
// Live filter over the item table.
const q = document.getElementById('q');
const rows = Array.from(document.querySelectorAll('#items tbody tr'));
q.addEventListener('input', () => {
  const needle = q.value.toLowerCase();
  for (const row of rows) {
    row.style.display = row.textContent.toLowerCase().includes(needle) ? '' : 'none';
  }
});
// Click a header to sort. Size sorts numerically via the data-bytes attribute.
document.querySelectorAll('#items th').forEach((th, index) => {
  let ascending = true;
  th.addEventListener('click', () => {
    const body = document.querySelector('#items tbody');
    const sorted = rows.slice().sort((a, b) => {
      const ca = a.children[index], cb = b.children[index];
      const na = ca.dataset.bytes, nb = cb.dataset.bytes;
      const result = (na !== undefined && nb !== undefined)
        ? Number(na) - Number(nb)
        : ca.textContent.localeCompare(cb.textContent);
      return ascending ? result : -result;
    });
    ascending = !ascending;
    for (const row of sorted) body.appendChild(row);
  });
});
</script>
</body>
</html>"""
        (result.GeneratedAtUtc.ToString "u")
        result.Platform
        result.Findings.Length
        result.ScanRoots.Length
        (Utilities.Format.bytes (Scanner.reclaimableBytes result))
        (Utilities.Format.bytes (Scanner.pendingReviewBytes result))
        (Utilities.Format.duration result.Elapsed)
        unreadableNote
        (donut categoryTotals)
        (treemap (largest |> List.truncate 12 |> List.map (fun f -> IO.Path.GetFileName f.Meta.Path, f.Meta.SizeBytes)))
        categoryRows
        rows

/// Writes the HTML report to disk.
let write (path: string) (result: Scanner.ScanResult) : Result<unit, string> =
    try
        let directory = IO.Path.GetDirectoryName path
        if not (String.IsNullOrEmpty directory) && not (IO.Directory.Exists directory) then
            IO.Directory.CreateDirectory directory |> ignore
        IO.File.WriteAllText(path, render result)
        Ok ()
    with ex -> Error (sprintf "cannot write %s: %s" path ex.Message)
