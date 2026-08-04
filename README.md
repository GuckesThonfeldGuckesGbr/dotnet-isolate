# dotnet-isolate
dotnet-isolate isolates the exact set of projects and dependencies required to build a .NET project from a larger solution. It helps reduce Docker build contexts, improve cache reuse, and speed up CI/CD pipelines without modifying the original solution.
