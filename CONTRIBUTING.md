# Contributing

## Commit messages

This repo follows [Conventional Commits](https://www.conventionalcommits.org/): the summary line
starts with a type, a colon, then a description in the imperative mood — say what applying the
commit *does*, not what was done.

```
<type>: <description>
```

Common types:
- `feat:` — a new feature
- `fix:` — a bug fix
- `docs:` — documentation only
- `chore:` — tooling, dependencies, repo config; no production code change
- `refactor:` — code change that neither fixes a bug nor adds a feature
- `test:` — adding or correcting tests
- `ci:` — CI/CD pipeline changes
- `perf:` — a performance improvement

Examples:
- `feat: add project reference graph resolution`
- `fix: preserve file timestamps when falling back to copy`
- `docs: document the docker integration patterns`
- `refactor: extract link-strategy probe into its own module`

Imperative mood means finishing the sentence "If applied, this commit will …":
`add unit test for X`, not `added a unit test for X` or `adds a unit test for X`.
