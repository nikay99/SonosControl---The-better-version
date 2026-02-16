using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SonosControl.DAL.Interfaces;

namespace SonosControl.DAL.Repos
{
    public class HolidayRepo : IHolidayRepo
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public HolidayRepo(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        }

        public async Task<bool> IsHoliday(CancellationToken cancellationToken = default)
        {
            var date = DateTime.Now.ToString("yyyy-MM-dd");
            var url = $"https://openholidaysapi.org/PublicHolidays?countryIsoCode=AT&validFrom={date}&validTo={date}&subdivisionIsoCode=AT-8&languageIsoCode=DE";

            var client = _httpClientFactory.CreateClient("HolidayApi");
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                return false;
            return root.GetArrayLength() > 0;
        }
    }
}
