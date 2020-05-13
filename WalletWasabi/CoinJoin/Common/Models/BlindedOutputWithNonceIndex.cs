using NBitcoin;
using Newtonsoft.Json;
using System;
using System.Diagnostics.CodeAnalysis;
using WalletWasabi.JsonConverters;

namespace WalletWasabi.CoinJoin.Common.Models
{
	public class BlindedOutputWithNonceIndex : IEquatable<BlindedOutputWithNonceIndex>
	{
		public BlindedOutputWithNonceIndex(int n, uint256 message)
		{
			N = n;
			Message = message;
		}

		public int N { get; set; }

		[JsonConverter(typeof(Uint256JsonConverter))]
		public uint256 Message { get; set; }

		public override bool Equals(object obj)
		{
			if (obj is BlindedOutputWithNonceIndex other)
			{
				return Equals(other);
			}
			return false;
		}

		public bool Equals([AllowNull] BlindedOutputWithNonceIndex other)
		{
			return other?.Message == this.Message;
		}

		public override int GetHashCode()
		{
			return Message.GetHashCode();
		}
	}
}
