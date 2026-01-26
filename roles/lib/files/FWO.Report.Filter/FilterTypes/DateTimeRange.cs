using FWO.Report.Filter.Ast;
using FWO.Report.Filter.Exceptions;

namespace FWO.Report.Filter.FilterTypes
{
    class DateTimeRange
    {
        public readonly DateTimeOffset? Start;
        public readonly DateTimeOffset? End;

        public DateTimeRange(AstNodeFilterDateTimeRange filter)
        {
            bool isSingleDate = DateTimeOffset.TryParse(filter.Value.Text, out DateTimeOffset time);
            int currentYear = DateTimeOffset.UtcNow.Year;
            int currentMonth = DateTimeOffset.UtcNow.Month;

            switch (filter.Operator.Kind)
            {
                case TokenKind.EEQ:
                case TokenKind.EQ:
                    switch (filter.Value.Text)
                    {
                        // todo: add today, yesterday, this week, last week
                        case "now":
                            DateTimeOffset now = DateTimeOffset.UtcNow;
                            Start = now;
                            End = now;
                            break;
                        case "this year":
                            Start = new DateTimeOffset(currentYear, 01, 01, 00, 00, 00, TimeSpan.Zero);
                            End = new DateTimeOffset(currentYear, 01, 01, 00, 00, 00, TimeSpan.Zero).AddYears(1);
                            break;
                        case "last year":
                            Start = new DateTimeOffset(currentYear, 01, 01, 00, 00, 00, TimeSpan.Zero).AddYears(-1);
                            End = new DateTimeOffset(currentYear, 01, 01, 00, 00, 00, TimeSpan.Zero);
                            break;
                        case "this month":
                            Start = new DateTimeOffset(currentYear, currentMonth, 01, 00, 00, 00, TimeSpan.Zero);
                            End = new DateTimeOffset(currentYear, currentMonth, 01, 00, 00, 00, TimeSpan.Zero).AddMonths(1);
                            break;
                        case "last month":
                            Start = new DateTimeOffset(currentYear, currentMonth, 01, 00, 00, 00, TimeSpan.Zero).AddMonths(-1);
                            End = new DateTimeOffset(currentYear, currentMonth, 01, 00, 00, 00, TimeSpan.Zero);
                            break;
                        default:
                            if (isSingleDate)
                            {
                                Start = time;
                                End = time;
                            }
                            else
                            {
                                throw new SyntaxException($"Wrong time range format.", filter.Value.Position); // Unexpected token
                            }
                            break;
                    }
                    break;
                case TokenKind.LSS:
                    End = time;
                    break;
                case TokenKind.GRT:
                    Start = time;
                    break;
                default:
                    throw new SemanticException($"Operator is not appliable for filter {filter.Name.Kind} of type {typeof(DateTimeRange)}", filter.Operator.Position);
            }
        }
    }
}
