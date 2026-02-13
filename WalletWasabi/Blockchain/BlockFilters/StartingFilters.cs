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
		if (network == Network.Main)
		{
			return FilterModel.Create(
				startingHeader.Height,
				startingHeader.BlockHash,
				Convert.FromHexString("02832810ec08a0"), // empty fake filter which will be ignored
				startingHeader.HeaderOrPrevBlockHash,
				startingHeader.EpochBlockTime);
		}
		if (network == Network.TestNet)
		{
			// First SegWit block with P2WPKH on TestNet: 00000000000f0d5edcaeba823db17f366be49a80d91d15b77747c2e017b8c20a
			return FilterModel.Create(
				startingHeader.Height,
				startingHeader.BlockHash,
				Convert.FromHexString("02832810ec08a0"),
				startingHeader.HeaderOrPrevBlockHash,
				startingHeader.EpochBlockTime);
		}
		if (network == Network.RegTest)
		{
			return FilterModel.Create(
				startingHeader.Height,
				startingHeader.BlockHash,
				Convert.FromHexString("02832810ec08a0"),
				startingHeader.HeaderOrPrevBlockHash,
				startingHeader.EpochBlockTime);
		}
		if (network == Bitcoin.Instance.Signet)
		{
			return FilterModel.Create(
				startingHeader.Height,
				startingHeader.BlockHash,
				Convert.FromHexString("02832810ec08a0"),
				startingHeader.HeaderOrPrevBlockHash,
				startingHeader.EpochBlockTime);
		}
		throw new NotSupportedNetworkException(network);
	}
}
