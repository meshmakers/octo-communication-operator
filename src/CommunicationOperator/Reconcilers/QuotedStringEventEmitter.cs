using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace Meshmakers.Octo.Communication.Operator.Reconcilers;

/// <summary>
/// YamlDotNet <see cref="IEventEmitter"/> wrapper that forces every string scalar
/// to be emitted with double quotes. Numbers and booleans pass through unchanged.
/// <para>
/// Purpose: keep Helm from re-interpreting all-digit string values (e.g. the
/// System.Communication-blueprint seed adapter RtId <c>670000000000000000000002</c>,
/// 24 decimal digits) as <c>float64</c> via Go YAML's Core schema. The blueprint
/// seed pattern collides with the YAML number resolver, so emitting the value as
/// a plain scalar makes Helm coerce it to <c>6.7e+23</c>, which then flows into
/// the rendered Deployment as <c>OCTO_ADAPTER__ADAPTERRTID=6.7e+23</c> and the
/// SDK rejects it as not a valid 24-digit hex ObjectId — the adapter never
/// registers and CommunicationState stays at <c>Unregistered</c>. Real
/// MongoDB-generated RtIds (contain a–f) survive the unquoted path; only the
/// blueprint-seeded all-digit ids trigger the float coercion.
/// </para>
/// <para>
/// We intentionally do <b>not</b> apply this to bools / numbers — quoting those
/// would change the chart-side semantics (a chart that branches on
/// <c>{{ if .Values.streamDataEnabled }}</c> would always be truthy if we
/// quoted <c>true</c> as the string "True").
/// </para>
/// </summary>
internal sealed class QuotedStringEventEmitter : ChainedEventEmitter
{
    public QuotedStringEventEmitter(IEventEmitter nextEmitter) : base(nextEmitter)
    {
    }

    public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
    {
        if (eventInfo.Source.Type == typeof(string))
        {
            eventInfo.Style = ScalarStyle.DoubleQuoted;
        }

        base.Emit(eventInfo, emitter);
    }
}
