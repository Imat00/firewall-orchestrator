using FWO.Data.DeviceSpecificSettings;
using FWO.Data.RulePositioning;
using FWO.Data.RulePositioning.CheckPoint;
using FWO.Data.Workflow;
using FWO.ExternalSystems.FirewallWrite;
using NUnit.Framework;
using System.Text.Json.Nodes;

namespace FWO.Test
{
    [TestFixture]
    [Parallelizable]
    internal class RulePositionPlacementProviderTest
    {
        private const ProvisioningPositioningAlgorithm kAlgorithm = ProvisioningPositioningAlgorithm.DefaultEndOfRulebase;

        private static WfImplTask TaskWith(RulePosition? position) => new() { Id = 7, RulePosition = position };

        [Test]
        public void WithoutPositionPlacesAtTheEndOfTheRulebase()
        {
            Assert.That(new RulePositionPlacementProvider().GetPlacement(TaskWith(null)), Is.Null);
        }

        [TestCase(CheckPointPositionAnchorKind.Top, null, FirewallRuleAnchor.Top)]
        [TestCase(CheckPointPositionAnchorKind.Bottom, null, FirewallRuleAnchor.Bottom)]
        [TestCase(CheckPointPositionAnchorKind.Above, "anchor-rule", FirewallRuleAnchor.Above)]
        [TestCase(CheckPointPositionAnchorKind.Below, "anchor-rule", FirewallRuleAnchor.Below)]
        public void CheckPointPositionKeepsAnchorAnchorRuleAndLayer(CheckPointPositionAnchorKind anchorKind, string? anchorRuleUid,
            FirewallRuleAnchor expectedAnchor)
        {
            CheckPointRulePosition position = new("layer-uid", anchorKind, anchorRuleUid, kAlgorithm);
            FirewallRulePlacement? placement = new RulePositionPlacementProvider().GetPlacement(TaskWith(position));
            Assert.That(placement, Is.EqualTo(new FirewallRulePlacement(expectedAnchor, anchorRuleUid, "layer-uid")));
        }

        [Test]
        public void UnknownPositionTypeIsNotWritten()
        {
            UnknownRulePosition position = new("other.rule-position.v1", 1, kAlgorithm, new JsonObject());
            FirewallWriteException? exception = Assert.Throws<FirewallWriteException>(() =>
                new RulePositionPlacementProvider().GetPlacement(TaskWith(position)));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(exception?.Error.Kind, Is.EqualTo(FirewallWriteErrorKind.Unsupported));
                Assert.That(exception?.Message, Does.Contain("other.rule-position.v1"));
            }
        }

        [Test]
        public void RulebaseKeyIsTheLayerOfACheckPointPositionOnly()
        {
            CheckPointRulePosition checkPoint = new("layer-uid", CheckPointPositionAnchorKind.Bottom, null, kAlgorithm);
            UnknownRulePosition unknown = new("other.rule-position.v1", 1, kAlgorithm, new JsonObject());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(checkPoint.GetRulebaseKey(), Is.EqualTo("layer-uid"));
                Assert.That(unknown.GetRulebaseKey(), Is.Null);
                Assert.That(System.Text.Json.JsonSerializer.Serialize<RulePosition>(checkPoint), Does.Not.Contain("rulebase"));
            }
        }

        [Test]
        public void PositionedRuleIsWrittenToItsLayerAndPosition()
        {
            CheckPointRulePosition position = new("layer-uid", CheckPointPositionAnchorKind.Below, "anchor-rule", kAlgorithm);
            FirewallRulePlacement? placement = new RulePositionPlacementProvider().GetPlacement(TaskWith(position));
            WfImplTask task = FirewallWriteTestSupport.Task(WfTaskType.access);
            IReadOnlyDictionary<string, FirewallPlaceholderValue> values = ImplTaskStepPlanner.ContextValues(task,
                FirewallWriteTestSupport.Context(FirewallVendors.kCheckPointR8x), placement);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(values[FirewallWritePlaceholders.LayerName].Value, Is.EqualTo("layer-uid"));
                Assert.That(values[FirewallWritePlaceholders.RulePosition].Value, Is.EqualTo("{\"below\":\"anchor-rule\"}"));
            }
        }
    }
}
