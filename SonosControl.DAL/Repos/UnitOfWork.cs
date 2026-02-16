using System;
using SonosControl.DAL.Interfaces;

namespace SonosControl.DAL.Repos
{
    public class UnitOfWork : IUnitOfWork
    {
        private readonly ISettingsRepo _settingsRepo;
        private readonly IHolidayRepo _holidayRepo;
        private readonly ISonosConnectorRepo _sonosConnectorRepo;

        public UnitOfWork(ISettingsRepo settingsRepo, IHolidayRepo holidayRepo, ISonosConnectorRepo sonosConnectorRepo)
        {
            _settingsRepo = settingsRepo ?? throw new ArgumentNullException(nameof(settingsRepo));
            _holidayRepo = holidayRepo ?? throw new ArgumentNullException(nameof(holidayRepo));
            _sonosConnectorRepo = sonosConnectorRepo ?? throw new ArgumentNullException(nameof(sonosConnectorRepo));
        }

        public ISettingsRepo SettingsRepo => _settingsRepo;
        public IHolidayRepo HolidayRepo => _holidayRepo;
        public ISonosConnectorRepo SonosConnectorRepo => _sonosConnectorRepo;
    }
}

