using NBitcoin;
using WalletWasabi.Backend.Models;
using WalletWasabi.Blockchain.Blocks;
using WalletWasabi.Exceptions;

namespace WalletWasabi.Blockchain.BlockFilters;

public static class StartingFilters
{
	public static FilterModel GetStartingFilter(Network network)
	{
		var startingHeader = SmartHeader.GetStartingHeader(network);
		// TODO: use the real filters to remove the HashCount in WalletFilterProcessor
		return new FilterModel(startingHeader, new GolombRiceFilter(Convert.FromHexString("02832810ec08a0")));
	}
}
