using NBitcoin;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Text;
using WalletWasabi.JsonConverters;

namespace WalletWasabi.CoinJoin.Common.Models
{
	public class BlindedOutputScript : IEquatable<BlindedOutputScript>
	{
		public BlindedOutputScript(int n, uint256 message)
		{
			N = n;
			Message = message;
		}

		public int N { get; set; }

		[JsonConverter(typeof(Uint256JsonConverter))]
		public uint256 Message { get; set; }

		public override bool Equals(object? obj)
		{
			if (obj is BlindedOutputScript other)
			{
				return Equals(other);
			}
			return false;
		}

		public bool Equals([AllowNull] BlindedOutputScript other)
		{
			return other?.Message == this.Message;
		}

		public override int GetHashCode()
		{
			return Message.GetHashCode();
		}
	}

	public class InputsRequest
	{
		[Required]
		public long RoundId { get; set; }

		[Required, MinLength(1)]
		public IEnumerable<InputProofModel> Inputs { get; set; }

		[Required, MinLength(1)]
		public IEnumerable<BlindedOutputScript> BlindedOutputScripts { get; set; }

		[Required]
		[JsonConverter(typeof(BitcoinAddressJsonConverter))]
		public BitcoinAddress ChangeOutputAddress { get; set; }

		public StringContent ToHttpStringContent()
		{
			string jsonString = JsonConvert.SerializeObject(this, Formatting.None);
			return new StringContent(jsonString, Encoding.UTF8, "application/json");
		}
	}
}
