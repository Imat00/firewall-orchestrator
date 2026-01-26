namespace FWO.Basics.Interfaces
{
    public interface IComplianceViolation
    {
        int RuleId { get; set; }
        DateTimeOffset FoundDate { get; set; }
        DateTimeOffset? RemovedDate { get; set; }
        string Details { get; set; }
        long RiskScore { get; set; }
        int PolicyId { get; set; }
        int CriterionId { get; set; }
    } 
}


