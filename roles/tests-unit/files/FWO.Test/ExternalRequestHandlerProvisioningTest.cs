using FWO.Api.Client;
using FWO.Api.Client.Queries;
using FWO.Data;
using FWO.Data.DeviceSpecificSettings;
using FWO.Data.Workflow;
using FWO.Middleware.Server;
using FWO.Services.Workflow;
using NUnit.Framework;

namespace FWO.Test
{
    /// <summary>
    /// Tests that starting the provisioning of a ticket creates the external request of the next work list entry only:
    /// a firewall write referencing its implementation task, or a request task handed to the external ticket system
    /// selected in the device specific settings; nothing while a request of the ticket runs or nothing is left.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    internal class ExternalRequestHandlerProvisioningTest
    {
        private const long kTicketId = 5;
        private const int kManagementId = 1;
        private const int kTufinSystemId = 1;
        private const long kImplTaskId = 10;
        private const int kOpenStateId = 210;
        private const int kDoneStateId = 631;

        private static readonly List<ExternalTicketSystem> kTicketSystems = [ExtTicketHandlerTest.ticketSystem];

        private static readonly WfReqElement kGroupMember = new()
        {
            Id = 1,
            RequestAction = RequestAction.create.ToString(),
            GroupName = "ARxx12345-100",
            Name = "AppServerX",
            IpString = "10.0.0.1/32",
            Field = ElemFieldType.source.ToString()
        };

        /// <summary>
        /// Returns the ticket of the test and the unfinished requests of the ticket, and records the created requests.
        /// </summary>
        private sealed class ProvisioningApiConn(WfTicket ticket) : ExtTicketHandlerTestApiConn
        {
            public List<ExternalRequest> UnfinishedRequests { get; } = [];

            public List<object> CreatedRequests { get; } = [];

            public override async Task<QueryResponseType> SendQueryAsync<QueryResponseType>(string query, object? variables = null, string? operationName = null, QueryChunkingOptions? chunkingOptions = null)
            {
                if (query == ExtRequestQueries.getUnfinishedRequestsOfTicket)
                {
                    return (QueryResponseType)(object)UnfinishedRequests;
                }
                if (typeof(QueryResponseType) == typeof(WfTicket))
                {
                    return (QueryResponseType)(object)ticket;
                }
                if (query == ExtRequestQueries.addExtRequest && variables != null)
                {
                    CreatedRequests.Add(variables);
                }
                return await base.SendQueryAsync<QueryResponseType>(query, variables, operationName, chunkingOptions);
            }
        }

        private static WfTicket Ticket(int implTaskStateId = kOpenStateId)
        {
            WfReqTask reqTask = new()
            {
                Id = 1,
                Title = "Group",
                TicketId = kTicketId,
                TaskNumber = 1,
                TaskType = WfTaskType.group_create.ToString(),
                ManagementId = kManagementId,
                StateId = kOpenStateId,
                Elements = [kGroupMember],
                AdditionalInfo = "{\"ConnId\":\"1\",\"GrpName\":\"ARxx12345-100\"}"
            };
            reqTask.ImplementationTasks.Add(new WfImplTask
            {
                Id = kImplTaskId,
                ReqTaskId = reqTask.Id,
                TaskNumber = 1,
                TaskType = reqTask.TaskType,
                ManagementId = kManagementId,
                StateId = implTaskStateId
            });
            return new WfTicket { Id = kTicketId, Title = "Ticket", Tasks = [reqTask] };
        }

        private static ExternalRequestHandler CreateHandler(ProvisioningApiConn apiConnection, GlobalDeviceSpecificSettings settings)
        {
            SimulatedUserConfig userConfig = new()
            {
                ExtTicketSystems = System.Text.Json.JsonSerializer.Serialize(kTicketSystems),
                ReqPriorities = ExtTicketHandlerTest.reqPrios,
                ModNamingConvention = ExtTicketHandlerTest.namingConvention
            };
            return new ExternalRequestHandler(userConfig, apiConnection, null)
            {
                SCTemplateSource = new FixedSCTemplateSource(ExtTicketHandlerTest.ticketSystem.Templates)
            }.WithProvisioningSettings(settings);
        }

        private static GlobalDeviceSpecificSettings ExternalTicketSystemSettings()
        {
            return new GlobalDeviceSpecificSettings
            {
                ImplementationMode = ProvisioningImplementationMode.RerouteToExternalTicketSystem,
                RerouteToExternalTicketSystem = kTufinSystemId
            };
        }

        private static object? Variable(object variables, string name)
        {
            return variables.GetType().GetProperty(name)?.GetValue(variables);
        }

        [Test]
        public async Task StartProvisioning_AutomaticTarget_CreatesAFirewallWriteRequest()
        {
            ProvisioningApiConn apiConnection = new(Ticket());
            using ExternalRequestHandler handler = CreateHandler(apiConnection, new GlobalDeviceSpecificSettings());

            bool result = await handler.StartProvisioning(kTicketId);

            Assert.That(result, Is.True);
            Assert.That(apiConnection.CreatedRequests, Has.Count.EqualTo(1));
            object request = apiConnection.CreatedRequests[0];
            Assert.Multiple(() =>
            {
                Assert.That(Variable(request, "implTaskId"), Is.EqualTo(kImplTaskId));
                Assert.That(Variable(request, "taskNumber"), Is.EqualTo(1));
                Assert.That(Variable(request, "extTicketSystem"), Is.EqualTo(""));
                Assert.That(Variable(request, "extTaskType"), Is.EqualTo(WfTaskType.group_create.ToString()));
                Assert.That(Variable(request, "extRequestState"), Is.EqualTo(ExtStates.ExtReqInitialized.ToString()));
                Assert.That(Variable(request, "extQueryVariables"), Is.EqualTo("{\"ManagementId\":[1]}"));
            });
        }

        [Test]
        public async Task StartProvisioning_ExternalManagement_HandsTheRequestTaskToTheSelectedSystem()
        {
            ProvisioningApiConn apiConnection = new(Ticket());
            using ExternalRequestHandler handler = CreateHandler(apiConnection, ExternalTicketSystemSettings());

            bool result = await handler.StartProvisioning(kTicketId);

            Assert.That(result, Is.True);
            Assert.That(apiConnection.CreatedRequests, Has.Count.EqualTo(1));
            ExternalTicketSystem storedSystem = System.Text.Json.JsonSerializer.Deserialize<ExternalTicketSystem>(apiConnection.AddedExtTicketSystem ?? "")
                ?? throw new AssertionException("No external ticket system stored");
            Assert.Multiple(() =>
            {
                Assert.That(Variable(apiConnection.CreatedRequests[0], "implTaskId"), Is.Null);
                Assert.That(storedSystem.Id, Is.EqualTo(kTufinSystemId));
            });
        }

        [Test]
        public async Task StartProvisioning_ExternalManagement_StoresTheTimeoutAndRetriesOfTheManagement()
        {
            ProvisioningApiConn apiConnection = new(Ticket());
            GlobalDeviceSpecificSettings settings = ExternalTicketSystemSettings();
            settings.RequestResponseTimeout = 60;
            settings.RequestMaxAttempts = 4;
            settings.RequestCyclesBetweenAttempts = 9;
            using ExternalRequestHandler handler = CreateHandler(apiConnection, settings);

            await handler.StartProvisioning(kTicketId);

            ExternalTicketSystem storedSystem = System.Text.Json.JsonSerializer.Deserialize<ExternalTicketSystem>(apiConnection.AddedExtTicketSystem ?? "")
                ?? throw new AssertionException("No external ticket system stored");
            Assert.Multiple(() =>
            {
                Assert.That(storedSystem.ResponseTimeout, Is.EqualTo(60));
                Assert.That(storedSystem.MaxAttempts, Is.EqualTo(4));
                Assert.That(storedSystem.CyclesBetweenAttempts, Is.EqualTo(9));
            });
        }

        [Test]
        public async Task StartProvisioning_SelectedSystemUnknown_Fails()
        {
            ProvisioningApiConn apiConnection = new(Ticket());
            GlobalDeviceSpecificSettings settings = ExternalTicketSystemSettings();
            settings.RerouteToExternalTicketSystem = kTufinSystemId + 1;
            using ExternalRequestHandler handler = CreateHandler(apiConnection, settings);

            bool result = await handler.StartProvisioning(kTicketId);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.False);
                Assert.That(apiConnection.CreatedRequests, Is.Empty);
            });
        }

        [Test]
        public async Task StartProvisioning_RequestOfTheTicketStillRunning_CreatesNothing()
        {
            ProvisioningApiConn apiConnection = new(Ticket());
            apiConnection.UnfinishedRequests.Add(new ExternalRequest { Id = 3, TicketId = kTicketId });
            using ExternalRequestHandler handler = CreateHandler(apiConnection, new GlobalDeviceSpecificSettings());

            bool result = await handler.StartProvisioning(kTicketId);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(apiConnection.CreatedRequests, Is.Empty);
            });
        }

        [Test]
        public async Task StartProvisioning_EverythingFinished_CreatesNothing()
        {
            ProvisioningApiConn apiConnection = new(Ticket(kDoneStateId));
            using ExternalRequestHandler handler = CreateHandler(apiConnection, new GlobalDeviceSpecificSettings());

            bool result = await handler.StartProvisioning(kTicketId);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(apiConnection.CreatedRequests, Is.Empty);
            });
        }

        [Test]
        public async Task StartProvisioning_UnknownScopeTask_Fails()
        {
            ProvisioningApiConn apiConnection = new(Ticket());
            using ExternalRequestHandler handler = CreateHandler(apiConnection, new GlobalDeviceSpecificSettings());

            bool result = await handler.StartProvisioning(kTicketId, 99);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.False);
                Assert.That(apiConnection.CreatedRequests, Is.Empty);
            });
        }

        [Test]
        public async Task StartProvisioning_ScopeTask_ProvisionsIt()
        {
            ProvisioningApiConn apiConnection = new(Ticket());
            using ExternalRequestHandler handler = CreateHandler(apiConnection, new GlobalDeviceSpecificSettings());

            bool result = await handler.StartProvisioning(kTicketId, 1);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.True);
                Assert.That(apiConnection.CreatedRequests, Has.Count.EqualTo(1));
            });
        }
    }
}
