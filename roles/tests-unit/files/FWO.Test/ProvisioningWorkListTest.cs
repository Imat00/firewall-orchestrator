using FWO.Data.DeviceSpecificSettings;
using FWO.Data.Workflow;
using FWO.Services.Workflow;
using NUnit.Framework;

namespace FWO.Test
{
    /// <summary>
    /// Tests which tasks the provisioning work list contains, in which order, and which entry is provisioned next.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    internal class ProvisioningWorkListTest
    {
        private const int kAutomaticManagementId = 1;
        private const int kExternalManagementId = 2;
        private const int kManualManagementId = 3;
        private const int kExternalWithoutSystemManagementId = 4;
        private const int kExternalTicketSystemId = 7;
        private const int kOpenStateId = 210;
        private const int kEndStateId = 249;
        private const int kDoneStateId = 631;
        private const int kPlannedStateId = 149;
        private const int kBeingPlannedStateId = 110;

        private static readonly List<ProvisioningWay> kExternalTicketSystemOnly = [ProvisioningWay.ExternalTicketSystem];
        private static readonly List<long> kExampleReqTaskIds = [1, 2, 3];
        private static readonly List<ProvisioningWay> kExampleWays =
            [ProvisioningWay.FirewallWrite, ProvisioningWay.ExternalTicketSystem, ProvisioningWay.FirewallWrite];
        private static readonly List<bool> kExampleFinished = [false, false, true];
        private static readonly List<long?> kOrderedImplTaskIds = [11, 12, 13];
        private static readonly List<long?> kFirstImplTask = [11];
        private static readonly List<long?> kSecondImplTask = [12];

        /// <summary>
        /// Settings for the tests: one management per implementation mode, rules and objects alike.
        /// </summary>
        private sealed class FixedModes(int? planningEndState = null) : IProvisioningModeSource
        {
            public ProvisioningImplementationMode GetImplementationMode(int managementId, bool isRuleTask)
            {
                return managementId switch
                {
                    kExternalManagementId or kExternalWithoutSystemManagementId => ProvisioningImplementationMode.RerouteToExternalTicketSystem,
                    kManualManagementId => ProvisioningImplementationMode.Manual,
                    _ => ProvisioningImplementationMode.Automatic
                };
            }

            public int? GetExternalTicketSystemId(int managementId)
            {
                return managementId == kExternalManagementId ? kExternalTicketSystemId : null;
            }

            public int GetImplementationEndState(string taskType)
            {
                return kEndStateId;
            }

            public int? GetPlanningEndState(string taskType)
            {
                return planningEndState;
            }
        }

        private static WfReqTask ReqTask(long id, int taskNumber, int? managementId, string taskType = nameof(WfTaskType.group_create), int stateId = kOpenStateId)
        {
            return new WfReqTask { Id = id, TicketId = 5, TaskNumber = taskNumber, TaskType = taskType, ManagementId = managementId, StateId = stateId };
        }

        private static WfImplTask AddImplTask(WfReqTask reqTask, long id, int taskNumber, int? managementId, int stateId = kOpenStateId)
        {
            WfImplTask implTask = new() { Id = id, ReqTaskId = reqTask.Id, TaskNumber = taskNumber, TaskType = reqTask.TaskType, ManagementId = managementId, StateId = stateId };
            reqTask.ImplementationTasks.Add(implTask);
            return implTask;
        }

        private static ProvisioningWorkList Build(params WfReqTask[] reqTasks)
        {
            return ProvisioningWorkList.Build(reqTasks.ToList(), new FixedModes());
        }

        /// <summary>Builds the work list with an active planning phase ending in the planned state.</summary>
        private static ProvisioningWorkList BuildWithPlanning(params WfReqTask[] reqTasks)
        {
            return ProvisioningWorkList.Build(reqTasks.ToList(), new FixedModes(kPlannedStateId));
        }

        private static List<long?> ImplTaskIds(ProvisioningWorkList workList)
        {
            return [.. workList.Items.Select(item => item.ImplTask?.Id)];
        }

        [Test]
        public void Build_TheExampleTicket_ProvisionsInOrderAndSkipsManualAndFinishedTasks()
        {
            WfReqTask first = ReqTask(1, 1, kAutomaticManagementId);
            AddImplTask(first, 11, 1, kAutomaticManagementId);
            AddImplTask(first, 12, 2, kManualManagementId);
            WfReqTask second = ReqTask(2, 2, kExternalManagementId);
            WfReqTask third = ReqTask(3, 3, kAutomaticManagementId, nameof(WfTaskType.group_modify));
            AddImplTask(third, 31, 1, kAutomaticManagementId, kDoneStateId);

            ProvisioningWorkList workList = Build(third, second, first);

            List<ProvisioningWorkItem> items = workList.Items;
            Assert.Multiple(() =>
            {
                Assert.That(items.Select(item => item.ReqTask.Id), Is.EqualTo(kExampleReqTaskIds));
                Assert.That(items.Select(item => item.Way), Is.EqualTo(kExampleWays));
                Assert.That(items.Select(item => item.IsFinished), Is.EqualTo(kExampleFinished));
                Assert.That(workList.Next?.ImplTask?.Id, Is.EqualTo(11));
                Assert.That(workList.Gaps, Is.Empty);
            });
        }

        [Test]
        public void Build_ImplementationTasksAreOrderedByTheirNumber()
        {
            WfReqTask reqTask = ReqTask(1, 1, kAutomaticManagementId);
            AddImplTask(reqTask, 13, 3, kAutomaticManagementId);
            AddImplTask(reqTask, 11, 1, kAutomaticManagementId);
            AddImplTask(reqTask, 12, 2, kAutomaticManagementId);

            ProvisioningWorkList workList = Build(reqTask);

            Assert.That(ImplTaskIds(workList), Is.EqualTo(kOrderedImplTaskIds));
        }

        [Test]
        public void Build_RequestTaskOfAnExternalManagement_IsHandedOverAsAWhole()
        {
            WfReqTask reqTask = ReqTask(1, 1, kExternalManagementId);
            AddImplTask(reqTask, 11, 1, kAutomaticManagementId);

            ProvisioningWorkList workList = Build(reqTask);

            Assert.Multiple(() =>
            {
                Assert.That(workList.Items.Select(item => item.Way), Is.EqualTo(kExternalTicketSystemOnly));
                Assert.That(workList.Items[0].ImplTask, Is.Null);
                Assert.That(workList.Items[0].ExternalTicketSystemId, Is.EqualTo(kExternalTicketSystemId));
            });
        }

        [Test]
        public void Build_RuleTaskOfAnExternalManagement_IsHandedOverAsAWhole()
        {
            WfReqTask reqTask = ReqTask(1, 1, kExternalManagementId, nameof(WfTaskType.access));

            ProvisioningWorkList workList = Build(reqTask);

            Assert.That(workList.Items.Select(item => item.Way), Is.EqualTo(kExternalTicketSystemOnly));
        }

        [Test]
        public void Build_ExternalManagementWithoutSystem_IsReportedAndNotProvisioned()
        {
            WfReqTask reqTask = ReqTask(1, 1, kExternalWithoutSystemManagementId);
            AddImplTask(reqTask, 11, 1, kAutomaticManagementId);

            ProvisioningWorkList workList = Build(reqTask);

            Assert.Multiple(() =>
            {
                Assert.That(workList.Items, Is.Empty);
                Assert.That(workList.Gaps, Has.Count.EqualTo(1));
                Assert.That(workList.Gaps[0].ImplTask, Is.Null);
                Assert.That(workList.Gaps[0].ManagementId, Is.EqualTo(kExternalWithoutSystemManagementId));
                Assert.That(workList.Gaps[0].Reason, Does.Contain("no external ticket system is selected"));
            });
        }

        [Test]
        public void Build_FinishedRequestTaskOfAnExternalManagementWithoutSystem_IsNotReported()
        {
            WfReqTask reqTask = ReqTask(1, 1, kExternalWithoutSystemManagementId, stateId: kDoneStateId);

            ProvisioningWorkList workList = Build(reqTask);

            Assert.That(workList.Gaps, Is.Empty);
        }

        [Test]
        public void Build_FinishedRequestTaskOfAnExternalManagement_IsFinished()
        {
            WfReqTask reqTask = ReqTask(1, 1, kExternalManagementId, stateId: kEndStateId);

            ProvisioningWorkList workList = Build(reqTask);

            Assert.Multiple(() =>
            {
                Assert.That(workList.Items[0].IsFinished, Is.True);
                Assert.That(workList.Next, Is.Null);
            });
        }

        [Test]
        public void Build_ImplementationTaskOnAnExternalTarget_IsReportedAndNotProvisioned()
        {
            WfReqTask reqTask = ReqTask(1, 1, kAutomaticManagementId);
            AddImplTask(reqTask, 11, 1, kExternalManagementId);

            ProvisioningWorkList workList = Build(reqTask);

            Assert.Multiple(() =>
            {
                Assert.That(workList.Items, Is.Empty);
                Assert.That(workList.Gaps, Has.Count.EqualTo(1));
                Assert.That(workList.Gaps[0].ImplTask?.Id, Is.EqualTo(11));
                Assert.That(workList.Gaps[0].ManagementId, Is.EqualTo(kExternalManagementId));
            });
        }

        [Test]
        public void Build_FinishedImplementationTaskOnAnExternalTarget_IsNotReported()
        {
            WfReqTask reqTask = ReqTask(1, 1, kAutomaticManagementId);
            AddImplTask(reqTask, 11, 1, kExternalManagementId, kDoneStateId);

            ProvisioningWorkList workList = Build(reqTask);

            Assert.That(workList.Gaps, Is.Empty);
        }

        [Test]
        public void Build_ImplementationTaskOnAManualTarget_IsLeftToAPerson()
        {
            WfReqTask reqTask = ReqTask(1, 1, kAutomaticManagementId);
            AddImplTask(reqTask, 11, 1, kManualManagementId);
            AddImplTask(reqTask, 12, 2, kAutomaticManagementId);

            ProvisioningWorkList workList = Build(reqTask);

            Assert.Multiple(() =>
            {
                Assert.That(ImplTaskIds(workList), Is.EqualTo(kSecondImplTask));
                Assert.That(workList.Gaps, Is.Empty);
            });
        }

        [Test]
        public void Build_ImplementationTaskWithoutManagement_IsNotProvisioned()
        {
            WfReqTask reqTask = ReqTask(1, 1, kAutomaticManagementId);
            AddImplTask(reqTask, 11, 1, null);

            ProvisioningWorkList workList = Build(reqTask);

            Assert.That(workList.Items, Is.Empty);
        }

        [Test]
        public void Build_RuleImplementationTasks_AreNotWrittenYet()
        {
            WfReqTask reqTask = ReqTask(1, 1, kAutomaticManagementId, nameof(WfTaskType.access));
            AddImplTask(reqTask, 11, 1, kAutomaticManagementId);

            ProvisioningWorkList workList = Build(reqTask);

            Assert.Multiple(() =>
            {
                Assert.That(workList.Items, Is.Empty);
                Assert.That(workList.Gaps, Is.Empty);
            });
        }

        [TestCase(nameof(WfTaskType.new_interface))]
        [TestCase(nameof(WfTaskType.generic))]
        public void Build_TaskTypeWithoutTarget_IsNeverProvisioned(string taskType)
        {
            WfReqTask reqTask = ReqTask(1, 1, kExternalManagementId, taskType);
            AddImplTask(reqTask, 11, 1, kAutomaticManagementId);

            ProvisioningWorkList workList = Build(reqTask);

            Assert.That(workList.Items, Is.Empty);
        }

        [Test]
        public void Build_ObjectTaskWithoutManagement_ProvisionsItsImplementationTasks()
        {
            WfReqTask reqTask = ReqTask(1, 1, null, nameof(WfTaskType.object_create));
            AddImplTask(reqTask, 11, 1, kAutomaticManagementId);

            ProvisioningWorkList workList = Build(reqTask);

            Assert.That(ImplTaskIds(workList), Is.EqualTo(kFirstImplTask));
        }

        [Test]
        public void Next_SkipsFinishedEntries()
        {
            WfReqTask reqTask = ReqTask(1, 1, kAutomaticManagementId);
            AddImplTask(reqTask, 11, 1, kAutomaticManagementId, kEndStateId);
            AddImplTask(reqTask, 12, 2, kAutomaticManagementId);

            ProvisioningWorkList workList = Build(reqTask);

            Assert.That(workList.Next?.ImplTask?.Id, Is.EqualTo(12));
        }

        [Test]
        public void Build_PlanningActive_RequestTaskBeingPlannedHoldsBackTheEntriesBehindIt()
        {
            WfReqTask beingPlanned = ReqTask(1, 1, kAutomaticManagementId, stateId: kBeingPlannedStateId);
            AddImplTask(beingPlanned, 11, 1, kAutomaticManagementId);
            WfReqTask planned = ReqTask(2, 2, kAutomaticManagementId, stateId: kPlannedStateId);
            AddImplTask(planned, 21, 1, kAutomaticManagementId);

            ProvisioningWorkList workList = BuildWithPlanning(beingPlanned, planned);

            Assert.Multiple(() =>
            {
                Assert.That(workList.Next?.ImplTask?.Id, Is.EqualTo(11));
                Assert.That(workList.Next?.WaitsForPlanning, Is.True);
                Assert.That(workList.Items[1].WaitsForPlanning, Is.False);
            });
        }

        [Test]
        public void Build_PlanningActive_RequestTaskOfAnExternalManagementIsHandedOverBeforeItIsPlanned()
        {
            WfReqTask reqTask = ReqTask(1, 1, kExternalManagementId, stateId: kBeingPlannedStateId);

            ProvisioningWorkList workList = BuildWithPlanning(reqTask);

            Assert.That(workList.Next?.WaitsForPlanning, Is.False);
        }

        [Test]
        public void Build_PlanningInactive_NothingWaitsForPlanning()
        {
            WfReqTask reqTask = ReqTask(1, 1, kAutomaticManagementId, stateId: kBeingPlannedStateId);
            AddImplTask(reqTask, 11, 1, kAutomaticManagementId);

            ProvisioningWorkList workList = Build(reqTask);

            Assert.That(workList.Next?.WaitsForPlanning, Is.False);
        }

        [Test]
        public void Next_IsNullWhenEverythingIsFinished()
        {
            WfReqTask reqTask = ReqTask(1, 1, kAutomaticManagementId);
            AddImplTask(reqTask, 11, 1, kAutomaticManagementId, kDoneStateId);

            ProvisioningWorkList workList = Build(reqTask);

            Assert.That(workList.Next, Is.Null);
        }
    }
}
