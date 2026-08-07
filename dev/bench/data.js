window.BENCHMARK_DATA = {
  "lastUpdate": 1786090689556,
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
          "id": "b3bf829b1d2467d0110a90fe9e8205bac8bc8814",
          "message": "docs: explain how files outside the solution folder are handled\n\nThe solution folder is the boundary for file resolution, and the two sides behave\ndifferently: explicitly referenced files are copied from anywhere (raising the\noutput root, which shifts every path in the isolated tree), while auto-discovered\nbuild files stop at the solution folder. Neither was written down, and both change\nwhat a Dockerfile's COPY paths need to look like.\n\nAlso notes that <Import Project=\"...\"/> is not followed, and restores the\n-s/--solution paragraph, which the workaround for out-of-solution build files\ndepends on.\n\nCo-Authored-By: Claude Opus 5 <noreply@anthropic.com>",
          "timestamp": "2026-08-07T09:06:31+02:00",
          "tree_id": "c558d422e33dd47e4d40262f54b24c93145b6f5d",
          "url": "https://github.com/GuckesThonfeldGuckesGbr/dotnet-isolate/commit/b3bf829b1d2467d0110a90fe9e8205bac8bc8814"
        },
        "date": 1786089156578,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "OrchardCore.Cms.Web isolate: included files",
            "value": 981,
            "unit": "files"
          },
          {
            "name": "OrchardCore.Cms.Web isolate: included projects",
            "value": 198,
            "unit": "projects"
          },
          {
            "name": "OrchardCore.Cms.Web isolate: wall-clock time",
            "value": 55790,
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
          "id": "9fa9d5369ce73315146b555ce46b555aadfdef54",
          "message": "chore: normalise the repository to LF and enforce it with .gitattributes\n\nExactly one tracked file was CRLF - DotnetIsolate.sln - because Visual Studio and\n`dotnet sln` write solution files that way. Everything else, including the\nDiamondWithIncludedFilesSln fixture that the integration tests build for real, was\nalready LF, which is the evidence that an LF .sln works fine.\n\n`* text=auto eol=lf` keeps it that way: when VS or `dotnet sln` next writes CRLF,\nGit normalises it on commit instead of producing a whole-file diff that buries the\none line that actually changed.\n\nNothing depends on the repository's own line endings. SolutionFile.filterSln\nnormalises CRLF to LF when it splits and re-joins with CRLF, so its output is\nunaffected by its input - the generated solution stays CRLF, matching what\n`dotnet sln add` produces. The CRLF literals in the test suites are F# escape\nsequences in LF source files, not literal CR bytes.\n\nCo-Authored-By: Claude Opus 5 <noreply@anthropic.com>",
          "timestamp": "2026-08-07T10:01:18+02:00",
          "tree_id": "482d88641622c5cb7c5880427ea0e5625a4b8a62",
          "url": "https://github.com/GuckesThonfeldGuckesGbr/dotnet-isolate/commit/9fa9d5369ce73315146b555ce46b555aadfdef54"
        },
        "date": 1786090689037,
        "tool": "customSmallerIsBetter",
        "benches": [
          {
            "name": "OrchardCore.Cms.Web isolate: included files",
            "value": 981,
            "unit": "files"
          },
          {
            "name": "OrchardCore.Cms.Web isolate: included projects",
            "value": 198,
            "unit": "projects"
          },
          {
            "name": "OrchardCore.Cms.Web isolate: wall-clock time",
            "value": 52836,
            "unit": "ms"
          }
        ]
      }
    ]
  }
}