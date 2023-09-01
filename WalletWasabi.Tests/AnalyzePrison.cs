using System.Collections.Generic;
using System.IO;
using System.Linq;
using NBitcoin;
using WalletWasabi.WabiSabi.Backend;
using WalletWasabi.WabiSabi.Backend.DoSPrevention;
using WalletWasabi.WabiSabi.Backend.Rounds.CoinJoinStorage;
using System.Threading.Channels;
using Xunit;

namespace WalletWasabi.Tests;

public class AnalyzePrison
{
	[Fact]
	public void AnalizeTest()
	{
		var config = new WabiSabiConfig("<FullPathTo>/WabiSabiConfig.json");
		var dosConfig = new DoSConfiguration(
			config.DoSSeverity.ToDecimal(MoneyUnit.BTC),
			config.DoSMinTimeForFailedToVerify,
			config.DoSMinTimeForCheating,
			config.DoSMinTimeInPrison,
			(decimal) config.DoSPenaltyFactorForDisruptingConfirmation,
			(decimal) config.DoSPenaltyFactorForDisruptingSigning,
			(decimal) config.DoSPenaltyFactorForDisruptingByDoubleSpending);

		var coinjoins = File.ReadAllLines("<FullPathTo>/CoinJoinIdStore.txt").Select(uint256.Parse);
		var coinjoinIdStore = new InMemoryCoinJoinIdStore(coinjoins);
		var prison = new Prison(dosConfig, coinjoinIdStore, Enumerable.Empty<Offender>(),
			Channel.CreateUnbounded<Offender>().Writer);

		// To follow one specific case, do this:
		var selected = OutPoint.Parse("d7043711a84f25f6a4332160b3c67b936b7627cebf7e1e65168b04422bcd9c7e-71");

		// You can do this or grouping them by outpoints or whatever you think is better for analysing the problem
		var offenders = File.ReadAllLines("<FullPathTo>/Prison.txt")
			.Select(Offender.FromStringLine)
			.Where(offender => offender.OutPoint == selected)
			.OrderBy(offender => offender.StartedTime );

		var history = new List<(OutPoint, Offender, DateTimeOffset, TimeSpan, DateTimeOffset)>();
		foreach (var offender in offenders)
		{
			if (prison.IsBanned(offender.OutPoint, offender.StartedTime))
			{
				// You can set a breakpoint here.
			}
			prison.Punish(offender);
			var bt = prison.GetBanTimePeriod(offender.OutPoint);
			history.Add((offender.OutPoint, offender, bt.StartTime, bt.Duration, bt.EndTime));
		}
	}
}
