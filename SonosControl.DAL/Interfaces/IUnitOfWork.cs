namespace SonosControl.DAL.Interfaces
{
    public interface IUnitOfWork
    {
        ISettingsRepo SettingsRepo { get; }
        ISonosConnectorRepo SonosConnectorRepo { get; }
        IHolidayRepo HolidayRepo { get; }
    }
}
