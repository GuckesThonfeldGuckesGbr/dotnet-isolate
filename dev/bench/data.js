window.BENCHMARK_DATA = {
  "lastUpdate": 1785960873695,
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
      },
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
          "id": "4990d0d5ccb20f9b13b40874450912367dc8637a",
          "message": "test: cover the default ./<ProjectName> output dir path in Pipeline.isolate\n\nIntegration coverage was sitting right at the 90% gate (90.24% locally);\nPipeline.fs's OutputDir=None branch (FR-6) was never exercised by any\nintegration test, since every existing PipelineTests case passes an\nexplicit -o. Brings integration coverage to 91.46%.\n\nCo-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>",
          "timestamp": "2026-08-05T21:53:22+02:00",
          "tree_id": "9e5895e3316eb72d2f1633a6bf0b11ed1b84ac96",
          "url": "https://github.com/GuckesThonfeldGuckesGbr/dotnet-isolate/commit/4990d0d5ccb20f9b13b40874450912367dc8637a"
        },
        "date": 1785960002707,
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
            "value": 54082,
            "unit": "ms"
          }
        ]
      },
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
          "id": "ad21540fcf0349f9c8f2332f04f9374fefd7f7d5",
          "message": "fix: make explicit-solution-path test assertion platform-agnostic\n\nCompared the resolved SolutionFile against a bare path literal, but\nresolveSolutionRoot runs the explicit path through Path.GetFullPath, which\non Windows resolves a drive-relative literal like \"\\elsewhere\\Other.sln\"\nonto the current drive (e.g. \"D:\\elsewhere\\Other.sln\") - failed on Windows\nCI, passed everywhere else. Resolve the expected value the same way.",
          "timestamp": "2026-08-05T22:09:06+02:00",
          "tree_id": "75fcf47e1a71ca3139741638293154da806aea01",
          "url": "https://github.com/GuckesThonfeldGuckesGbr/dotnet-isolate/commit/ad21540fcf0349f9c8f2332f04f9374fefd7f7d5"
        },
        "date": 1785960873035,
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
            "value": 57211,
            "unit": "ms"
          }
        ]
      }
    ]
  }
}