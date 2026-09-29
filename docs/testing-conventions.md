---
description: TUnit and NSubstitute conventions for this repo - layout, namespaces, the analyzer rules that are errors, and what a new test project must set.
applies_to: tests/**
---

# Testing Conventions

- **Framework**: TUnit (`[Test]`, `Assert.That(...).IsXxx(...)`), NSubstitute for mocking.
- **Layout**: tests mirror the source folders (`Webhooks/`, `Services/`, `Common/`, `Finalizer/`).
- **Namespaces**: `Meshmakers.Octo.Communication.Operator.Tests.<Area>`.
- **Async throws on substitutes**: use `ThrowsAsync(...)`, not `Throws(...)`, for `Task`-returning
  members - `NS5003` is an error.
- **Disposable SUTs**: implement `IDisposable` on the test class and dispose the field, or TUnit
  raises `TUnit0023`.
- **`OperatorOptions` in tests**: `Microsoft.Extensions.Options.Options.Create(new OperatorOptions { ... })`,
  fully qualified - `using ...Operator.Options;` shadows `Options.Create`.
- **New test projects** must set `<EnablePreviewFeatures>true</EnablePreviewFeatures>` (KubeOps APIs
  are `[RequiresPreviewFeatures]`, CA2252).
