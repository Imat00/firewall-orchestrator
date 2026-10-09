using FWO.Api.Client.Queries;
using FWO.Data;
using FWO.Data.Workflow;
using FWO.Logging;

namespace FWO.Services.Workflow
{
    /// <summary>
    /// Creates object tasks (groups and single objects) per target management of the provisioning settings and
    /// hosts the action creating the missing implementation tasks of a ticket.
    /// </summary>
    public partial class WfHandler
    {
        private const string kImplTaskTargetsLogTitle = "Implementation Tasks";

        /// <summary>
        /// Creates the implementation tasks still missing for every open request task of a ticket. Existing
        /// implementation tasks are kept; a gateway or management that already has one is skipped.
        /// </summary>
        /// <param name="ticket">Ticket whose request tasks get their implementation tasks.</param>
        /// <returns>The number of created implementation tasks.</returns>
        public async Task<int> CreateMissingImplTasksForTicket(WfTicket ticket)
        {
            SetTicketEnv(ticket);
            int previousCount = CountImplTasks(ActTicket);
            List<WfReqTask> openTasks = [.. ActTicket.Tasks.Where(RequestTaskIsOpen)];
            foreach (WfReqTask task in await RequestTasksForInitialImplCreation(openTasks))
            {
                await AutoCreateImplTasks(task);
                SyncActTicketFromReqTask(task);
            }
            int createdCount = Math.Max(0, CountImplTasks(ActTicket) - previousCount);
            Log.WriteInfo(kImplTaskTargetsLogTitle, $"Created {createdCount} implementation tasks for ticket {ActTicket.Id}, " +
                $"considering {CountBundles(ActTicket)} task bundles.");
            return createdCount;
        }

        private bool RequestTaskIsOpen(WfReqTask reqTask)
        {
            return stateMatrixDict.Matrices.TryGetValue(reqTask.TaskType, out StateMatrix? matrix)
                && reqTask.StateId < matrix.MinTicketCompleted;
        }

        private static int CountImplTasks(WfTicket ticket)
        {
            return ticket.Tasks.Sum(task => task.ImplementationTasks.Count);
        }

        private int CountBundles(WfTicket ticket)
        {
            if (!userConfig.ReqConsiderBundling)
            {
                return 0;
            }
            return ticket.Tasks.Select(task => task.GetAddInfoValue(AdditionalInfoKeys.FlowBundleId))
                .Where(bundleId => !string.IsNullOrWhiteSpace(bundleId)).Distinct().Count();
        }

        /// <summary>
        /// Creates one implementation task per target management of an object task, i.e. a group task
        /// (group_create, group_modify, group_delete) or a single object task (object_create, object_modify).
        /// Without a request management the single task without management of the previous behaviour is created.
        /// </summary>
        private async Task AutoCreateObjectImplTasks(WfReqTask reqTask)
        {
            if (reqTask.ManagementId is not int managementId || managementId <= 0)
            {
                if (reqTask.ImplementationTasks.Count == 0)
                {
                    await CreateGenericImplTask(reqTask);
                }
                return;
            }

            List<Management> targets = await GetObjectTargetManagements(reqTask, managementId);
            List<Management> missingTargets = [.. targets.Where(target =>
                reqTask.ImplementationTasks.All(implTask => (implTask.ManagementId ?? reqTask.ManagementId) != target.Id))];
            bool adaptTitle = targets.Count > 1 || targets.Any(target => target.Id != managementId);
            WfTicket? storedTicket = await LoadTicketForImplTaskCreation(reqTask, missingTargets.Count);
            foreach (Management target in missingTargets)
            {
                await CreateObjectImplTask(reqTask, target, adaptTitle, storedTicket);
            }
        }

        /// <summary>
        /// Creates the implementation task of an object task on one target management.
        /// </summary>
        private async Task CreateObjectImplTask(WfReqTask reqTask, Management target, bool adaptTitle, WfTicket? previousTicket)
        {
            WfImplTask newImplTask = new(reqTask)
            {
                TaskNumber = reqTask.HighestImplTaskNumber() + 1,
                StateId = reqTask.StateId,
                ManagementId = target.Id
            };
            if (adaptTitle && !string.IsNullOrWhiteSpace(target.Name))
            {
                newImplTask.Title += ": " + target.Name;
            }
            if (dbAcc != null)
            {
                newImplTask.Id = await dbAcc.AddImplTaskToDb(newImplTask, previousTicket);
            }
            reqTask.ImplementationTasks.Add(newImplTask);
        }

        /// <summary>
        /// Resolves the target managements of an object task with the role of the api connection. If that fails,
        /// the request management itself is the only target, as before.
        /// </summary>
        private async Task<List<Management>> GetObjectTargetManagements(WfReqTask reqTask, int managementId)
        {
            if (apiConnection != null)
            {
                try
                {
                    return await new ImplTaskManagementResolver(apiConnection).ResolveObjectTargets(managementId,
                        WfObjectTaskHelper.OrdersServiceObject(reqTask));
                }
                catch (Exception exception)
                {
                    Log.WriteError(kImplTaskTargetsLogTitle, $"Could not resolve the target managements of request task {reqTask.Id}, " +
                        $"using management {managementId}.", exception);
                }
            }
            return [new() { Id = managementId }];
        }

        /// <summary>
        /// Loads the gateways if they are not loaded yet, which is the case when actions run in the middleware.
        /// </summary>
        private async Task EnsureDevicesLoaded()
        {
            if (Devices.Count > 0 || apiConnection == null)
            {
                return;
            }
            try
            {
                Devices = await apiConnection.SendQueryAsync<List<Device>>(DeviceQueries.getDeviceDetails);
            }
            catch (Exception exception)
            {
                Log.WriteError(kImplTaskTargetsLogTitle, "Could not load the gateways for the implementation tasks.", exception);
            }
        }
    }
}
