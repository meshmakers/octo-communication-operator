using Meshmakers.Octo.Communication.Operator.Helm;

namespace Meshmakers.Octo.Communication.Operator.Tests.Helm;

/// <summary>
///     AB#5325 — recognising a server-side-apply field-ownership conflict.
/// </summary>
/// <remarks>
///     🔴 The two inputs below are <b>verbatim helm stderr</b> captured on 2026-09-23, on the
///     operator's own Deployment (patched with <c>kubectl set image</c>) and on an adapter pool member
///     (patched with <c>kubectl set env</c>). A parser tested against text someone invented for the
///     test proves only that the invention is understood — and the multi-conflict shape in particular
///     (bulleted list, "conflicts" plural, the rollback repeating it) is not what one guesses from the
///     single-conflict case.
/// </remarks>
internal class HelmFieldOwnershipConflictTests
{
    /// <summary>The operator's own Deployment, one contested field, after `kubectl set image`.</summary>
    private const string SingleConflictStdErr =
        """
        level=WARN msg="upgrade failed" name=octo-operator error="conflict occurred while applying object octo-operator-system/communication-operator apps/v1, Kind=Deployment: Apply failed with 1 conflict: conflict with \"kubectl-set\" using apps/v1: .spec.template.spec.containers[name=\"octo-mesh-communication-operator\"].image"
        Error: UPGRADE FAILED: conflict occurred while applying object octo-operator-system/communication-operator apps/v1, Kind=Deployment: Apply failed with 1 conflict: conflict with "kubectl-set" using apps/v1: .spec.template.spec.containers[name="octo-mesh-communication-operator"].image
        """;

    /// <summary>A pool member, three contested env values, and the rollback failing the same way.</summary>
    private const string MultiConflictStdErr =
        """
        level=INFO msg="warning: destination for octo-mesh-adapter.secrets.rabbitmq is a table. Ignoring non-table value ()"
        level=WARN msg="upgrade failed" name=accounting-49240000000000000000aa01 error="conflict occurred while applying object octo/accounting-49240000000000000000aa01 apps/v1, Kind=Deployment: Apply failed with 1 conflict: conflict with \"kubectl-set\" using apps/v1: .spec.template.spec.containers[name=\"mesh-adapter\"].env[name=\"OCTO_SYSTEM__DATABASEHOST\"].value"
        level=WARN msg="Rollback \"accounting-49240000000000000000aa01\" failed: conflict occurred while applying object octo/accounting-49240000000000000000aa01 apps/v1, Kind=Deployment: Apply failed with 3 conflicts: conflicts with \"kubectl-set\" using apps/v1:
        - .spec.template.spec.containers[name=\"mesh-adapter\"].env[name=\"OCTO_ADAPTER__BROKERHOST\"].value
        - .spec.template.spec.containers[name=\"mesh-adapter\"].env[name=\"OCTO_ADAPTER__STREAMDATAHOST\"].value
        - .spec.template.spec.containers[name=\"mesh-adapter\"].env[name=\"OCTO_SYSTEM__DATABASEHOST\"].value"
        Error: UPGRADE FAILED: an error occurred while rolling back the release. original upgrade error: conflict occurred while applying object octo/accounting-49240000000000000000aa01 apps/v1, Kind=Deployment: Apply failed with 1 conflict: conflict with "kubectl-set" using apps/v1: .spec.template.spec.containers[name="mesh-adapter"].env[name="OCTO_SYSTEM__DATABASEHOST"].value
        """;

    /// <summary>
    ///     🔴 Captured from the first LIVE run of this code, 2026-09-24 — and it found three defects the
    ///     two samples above did not. Helm's <c>error="…"</c> field carries its line breaks as the two
    ///     characters <c>\n</c>, so stripping every backslash glued the next word onto the path
    ///     (<c>…DATABASEHOST"].valuenconflicts</c>); trailing <c>"</c> and <c>:</c> made one field look
    ///     like three; and the manager here is <c>unknown</c> — the operator's OWN scale patch, which
    ///     named no field manager, not a person with kubectl.
    /// </summary>
    private const string LiveTwoManagerStdErr =
        """
        level=WARN msg="upgrade failed" name=accounting-49240000000000000000aa01 error="conflict occurred while applying object octo/accounting-49240000000000000000aa01 apps/v1, Kind=Deployment: Apply failed with 2 conflicts: conflicts with \"kubectl-set\" using apps/v1:\n- .spec.template.spec.containers[name=\"mesh-adapter\"].env[name=\"OCTO_SYSTEM__DATABASEHOST\"].value\nconflicts with \"unknown\" using apps/v1:\n- .spec.replicas"
        Error: UPGRADE FAILED: an error occurred while rolling back the release. original upgrade error: conflict occurred while applying object octo/accounting-49240000000000000000aa01 apps/v1, Kind=Deployment: Apply failed with 2 conflicts: conflicts with "kubectl-set" using apps/v1:
        - .spec.template.spec.containers[name="mesh-adapter"].env[name="OCTO_SYSTEM__DATABASEHOST"].value
        """;

    [Test]
    public async Task TheLiveShape_NamesBothManagersAndNoGluedWords()
    {
        var message = HelmFieldOwnershipConflict.Explain("accounting-49240000000000000000aa01", "octo",
            LiveTwoManagerStdErr);

        using var _ = Assert.Multiple();
        await Assert.That(HelmFieldOwnershipConflict.ForeignManagers(LiveTwoManagerStdErr).Count).IsEqualTo(2);
        // The defect: the escaped line break was stripped and glued the next word on.
        await Assert.That(message).DoesNotContain("valuenconflicts");
        await Assert.That(message).Contains("OCTO_SYSTEM__DATABASEHOST");
        await Assert.That(message).Contains(".spec.replicas");
        // …and only once, not as .spec.replicas / .spec.replicas" / .spec.replicas:
        await Assert.That(message).DoesNotContain(".spec.replicas\"");
        await Assert.That(message).DoesNotContain(".spec.replicas:");
    }

    /// <summary>
    ///     🔴 The attribution has to be right or the message is worse than none: the first live run told
    ///     an operator that a person had patched a field the operator itself had written.
    /// </summary>
    [Test]
    public async Task AnUnnamedManager_IsExplainedAsSuchAndNotBlamedOnAPerson()
    {
        var message = HelmFieldOwnershipConflict.Explain("rel", "octo", LiveTwoManagerStdErr);

        using var _ = Assert.Multiple();
        await Assert.That(message).Contains("the name Kubernetes records when a writer sets no");
        await Assert.That(message).Contains("most likely a past scale rather than a person");
        // The kubectl half is still called what it is, because this stderr has both.
        await Assert.That(message).Contains("hand-written change");
    }

    [Test]
    public async Task TheOperatorsOwnManager_IsNamedAsItsOwnOutOfBandWrite()
    {
        const string stdErr =
            """
            Error: UPGRADE FAILED: Apply failed with 1 conflict: conflict with "octo-communication-operator" using apps/v1: .spec.replicas
            """;

        var message = HelmFieldOwnershipConflict.Explain("rel", "octo", stdErr);

        using var _ = Assert.Multiple();
        await Assert.That(message).Contains("this operator's own out-of-band write");
        await Assert.That(message).Contains("AB#5350");
        await Assert.That(message).DoesNotContain("hand-written change");
    }

    [Test]
    public async Task ASingleConflict_IsRecognisedAndNamesItsManager()
    {
        using var _ = Assert.Multiple();
        await Assert.That(HelmFieldOwnershipConflict.IsFieldOwnershipConflict(SingleConflictStdErr)).IsTrue();
        await Assert.That(HelmFieldOwnershipConflict.ForeignManagers(SingleConflictStdErr))
            .IsEquivalentTo(new[] { "kubectl-set" });
    }

    /// <summary>
    ///     The plural spelling ("conflicts with") and the bulleted list are the shapes the rollback
    ///     produces, and the manager must still be reported exactly once.
    /// </summary>
    [Test]
    public async Task TheRollbacksPluralShape_IsRecognisedAndTheManagerIsNotDuplicated()
    {
        using var _ = Assert.Multiple();
        await Assert.That(HelmFieldOwnershipConflict.IsFieldOwnershipConflict(MultiConflictStdErr)).IsTrue();
        await Assert.That(HelmFieldOwnershipConflict.ForeignManagers(MultiConflictStdErr).Count).IsEqualTo(1);
    }

    [Test]
    public async Task TheExplanationNamesTheManagerTheCauseAndBothRemedies()
    {
        var message = HelmFieldOwnershipConflict.Explain("accounting-49240000000000000000aa01", "octo",
            MultiConflictStdErr);

        using var _ = Assert.Multiple();
        await Assert.That(message).Contains("kubectl-set");
        // The cause, in the words an operator would search for.
        await Assert.That(message).Contains("kubectl set image");
        // Remedy one.
        await Assert.That(message).Contains("delete the Deployment");
        // Remedy two, named as the option it actually is.
        await Assert.That(message).Contains("Helm.ForceConflicts");
        // The fact that makes retrying the wrong move.
        await Assert.That(message).Contains("'failed' state");
        await Assert.That(message).Contains("accounting-49240000000000000000aa01");
    }

    [Test]
    public async Task TheExplanationNamesTheContestedFields()
    {
        var message = HelmFieldOwnershipConflict.Explain("release", "octo", MultiConflictStdErr);

        using var _ = Assert.Multiple();
        await Assert.That(message).Contains("OCTO_ADAPTER__BROKERHOST");
        await Assert.That(message).Contains("OCTO_SYSTEM__DATABASEHOST");
    }

    /// <summary>
    ///     🔴 A conflict with helm's OWN manager is not a hand patch. Reporting it as one would send an
    ///     operator looking for a person who was never there — and helm does contend with itself, e.g.
    ///     between a release and a chart-managed sub-resource.
    /// </summary>
    [Test]
    public async Task AConflictWithHelmItself_IsNotReportedAsAHandPatch()
    {
        const string stdErr =
            """
            Error: UPGRADE FAILED: Apply failed with 1 conflict: conflict with "helm" using apps/v1: .spec.replicas
            """;

        using var _ = Assert.Multiple();
        await Assert.That(HelmFieldOwnershipConflict.IsFieldOwnershipConflict(stdErr)).IsFalse();
        await Assert.That(HelmFieldOwnershipConflict.ForeignManagers(stdErr)).IsEmpty();
    }

    /// <summary>Two different managers on one object are both named.</summary>
    [Test]
    public async Task SeveralForeignManagers_AreAllNamed()
    {
        const string stdErr =
            """
            Error: UPGRADE FAILED: Apply failed with 2 conflicts: conflict with "kubectl-set" using apps/v1: .spec.template.spec.containers[name="a"].image
            conflict with "kubectl-patch" using apps/v1: .spec.replicas
            """;

        var managers = HelmFieldOwnershipConflict.ForeignManagers(stdErr);

        using var _ = Assert.Multiple();
        await Assert.That(managers.Count).IsEqualTo(2);
        await Assert.That(managers).Contains("kubectl-patch");
    }

    /// <summary>
    ///     Every other helm failure must fall through untouched — an ImagePullBackOff or a missing
    ///     chart still needs the diagnostics path, and hijacking it here would replace a real
    ///     root cause with a lecture about field managers.
    /// </summary>
    [Test]
    [Arguments("Error: UPGRADE FAILED: context deadline exceeded")]
    [Arguments("Error: chart \"octo-mesh-adapter\" version \"9.9.9\" not found in octo-d808d9351847 index")]
    [Arguments("Error: UPGRADE FAILED: another operation (install/upgrade/rollback) is in progress")]
    [Arguments("")]
    [Arguments(null)]
    public async Task AnUnrelatedFailure_IsNotAFieldOwnershipConflict(string? stdErr)
    {
        await Assert.That(HelmFieldOwnershipConflict.IsFieldOwnershipConflict(stdErr)).IsFalse();
    }
}
