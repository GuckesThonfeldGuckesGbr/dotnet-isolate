window.BENCHMARK_DATA = {
  "lastUpdate": 1785946368126,
  "repoUrl": "https://github.com/GuckesThonfeldGuckesGbr/dotnet-isolate",
  "entries": {
    "Benchmark": [
      {
        "commit": {
          "author": {
            "email": "ctg@baggerbagger.de",
            "name": "Christopher Thonfeld-Guckes",
            "username": "cguckes"
          },
          "committer": {
            "email": "ctg@baggerbagger.de",
            "name": "Christopher Thonfeld-Guckes",
            "username": "cguckes"
          },
          "distinct": true,
          "id": "e427439ffa66e73560a3236918e1cd5556a4d745",
          "message": "ci: track the OrchardCore performance benchmark on every push to main and every PR\n\nAdds a `performance` job to build.yml that runs DotnetIsolate.PerformanceTests and feeds its\nresult into benchmark-action/github-action-benchmark, charting wall-clock time and graph\nsize on the gh-pages branch over commits - so a regression is visible as a trend rather\nthan only by eye in one run's console output.\n\n- BenchmarkResults.fs writes results in the customSmallerIsBetter JSON shape the action\n  expects, to a path overridable via DOTNET_ISOLATE_BENCHMARK_OUTPUT (an absolute path in\n  CI, since the test host's working directory isn't reliably the repo root).\n- Informational only, matching PR-1's rewritten O(depth) framing: no hard threshold\n  (alert-threshold 200%, fail-on-alert false), and not one of publish's dependencies.\n- History is only persisted (auto-push) on a real push to main; PR runs compare against\n  the existing history without writing to it, since PRs (especially from forks) shouldn't\n  get to rewrite the recorded baseline.\n\nCo-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>",
          "timestamp": "2026-08-05T17:38:12+02:00",
          "tree_id": "5228eb5b4ee6ecf3ea77d061da994d2175ab4036",
          "url": "https://github.com/GuckesThonfeldGuckesGbr/dotnet-isolate/commit/e427439ffa66e73560a3236918e1cd5556a4d745"
        },
        "date": 1785946367571,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "OrchardCore.Cms.Web isolate: included files",
            "value": 978,
            "unit": "files"
          },
          {
            "name": "OrchardCore.Cms.Web isolate: included projects",
            "value": 198,
            "unit": "projects"
          },
          {
            "name": "OrchardCore.Cms.Web isolate: wall-clock time",
            "value": 56095,
            "unit": "ms"
          }
        ]
      }
    ]
  }
}