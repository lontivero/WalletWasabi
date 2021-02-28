using System.Collections.Generic;
using System.Linq;

namespace WalletWasabi.WebClients.PayJoin
{
	public class PayJoinReceiverHelper
	{
		static IEnumerable<(PayJoinReceiverWellknownErrors EnumValue, string ErrorCode, string Message)> Get()
		{
			yield return (PayJoinReceiverWellknownErrors.Unavailable, "unavailable", "The PayJoin endpoint is not available for now.");
			yield return (PayJoinReceiverWellknownErrors.NotEnoughMoney, "not-enough-money", "The receiver added some inputs but could not bump the fee of the PayJoin proposal.");
			yield return (PayJoinReceiverWellknownErrors.VersionUnsupported, "version-unsupported", "This version of PayJoin is not supported.");
			yield return (PayJoinReceiverWellknownErrors.OriginalPSBTRejected, "original-psbt-rejected", "The receiver rejected the original PSBT.");
		}
		public static string GetErrorCode(PayJoinReceiverWellknownErrors err)
		{
			return Get().Single(o => o.EnumValue == err).ErrorCode;
		}
		public static PayJoinReceiverWellknownErrors? GetWellknownError(string errorCode)
		{
			var t = Get().FirstOrDefault(o => o.ErrorCode == errorCode);
			if (t == default)
				return null;
			return t.EnumValue;
		}
		static readonly string UnknownError = "Unknown error from the receiver";
		public static string GetMessage(string errorCode)
		{
			return Get().FirstOrDefault(o => o.ErrorCode == errorCode).Message ?? UnknownError;
		}
		public static string GetMessage(PayJoinReceiverWellknownErrors err)
		{
			return Get().Single(o => o.EnumValue == err).Message;
		}
	}
}