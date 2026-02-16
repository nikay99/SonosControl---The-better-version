using System.Threading;
using System.Threading.Tasks;

namespace SonosControl.DAL.Interfaces
{
    public interface IHolidayRepo
    {
        Task<bool> IsHoliday(CancellationToken cancellationToken = default);
    }
}