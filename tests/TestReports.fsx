(**
    Tests for Report.Markdown and Report.Html.
*)
open System
open TestFramework
open Types

/// Builds a minimal ScanResult for rendering tests.
let private sample () : Scanner.ScanResult =
    let meta path size =
        { Path = path; SizeBytes = size
          ApparentBytes = size
          LastAccess = DateTime.UtcNow; LastModified = DateTime.UtcNow
          Created = DateTime.UtcNow; IsDirectory = false
          LinkTarget = None; Owner = "u"; Mode = "rw-r--r--" }
    { GeneratedAtUtc = DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc)
      Platform = MacOS
      ScanRoots = [ "/cache" ]
      Findings =
        [ { Meta = meta "/cache/a.tmp" 4096L; RuleName = "Cache"; Category = Caches
            Risk = Safe; Recoverability = Regenerated; Strategy = ContentsOnly
            Reason = "regenerated"; Candidate = None }
          { Meta = meta "/cache/<script>.tmp" 2048L; RuleName = "Cache"; Category = Caches
            Risk = Review; Recoverability = Unrecoverable; Strategy = MatchingFiles
            Reason = "needs review"; Candidate = None } ]
      Duplicates = []
      Errors = []
      Elapsed = TimeSpan.FromSeconds 2.0
      Counters = Diagnostics.snapshot (Diagnostics.create ()) }

/// Runs all tests for the Markdown and HTML report modules.
let run () =
    suite "Report.Markdown"

    test "renders a heading, totals, and a category table" (fun () ->
        let md = Report.Markdown.render (sample ())
        assertContains "# DiskCleanupAdvisor" md "heading"
        assertContains "| Category" md "category table"
        assertContains "Caches" md "category name")

    test "distinguishes reclaimable from pending review" (fun () ->
        let md = Report.Markdown.render (sample ())
        assertContains "Reclaimable" md "reclaimable figure"
        assertContains "Pending review" md "pending figure")

    suite "Report.Html"

    test "is a complete self-contained document" (fun () ->
        let html = Report.Html.render (sample ())
        assertContains "<!DOCTYPE html>" html "doctype"
        assertContains "</html>" html "closing tag"
        assertContains "<style>" html "inline styles")

    test "references no external resource" (fun () ->
        // A CDN reference would break the file offline and leak the scan
        // to a third party the moment it is opened.
        let html = Report.Html.render (sample ())
        assertFalse (html.Contains "http://") "no http references"
        assertFalse (html.Contains "https://") "no https references"
        assertFalse (html.Contains "<script src") "no external scripts")

    test "escapes HTML metacharacters in paths" (fun () ->
        // A filename can legitimately contain < or &.
        let html = Report.Html.render (sample ())
        assertFalse (html.Contains "/cache/<script>.tmp") "raw path not emitted"
        assertContains "&lt;script&gt;" html "path is escaped")

    test "supports dark mode" (fun () ->
        assertContains "prefers-color-scheme" (Report.Html.render (sample ())) "dark mode query")

    test "includes an SVG chart" (fun () ->
        let html = Report.Html.render (sample ())
        assertContains "<svg" html "svg present")
