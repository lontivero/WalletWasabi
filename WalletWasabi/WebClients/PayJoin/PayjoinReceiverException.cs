
namespace WalletWasabi.WebClients.PayJoin
{
	public class PayJoinReceiverException : PayJoinException
	{
		public PayJoinReceiverException(string errorCode, string receiverMessage) : base(FormatMessage(errorCode, receiverMessage))
		{
			ErrorCode = errorCode;
			ReceiverMessage = receiverMessage;
			WellknownError = PayJoinReceiverHelper.GetWellknownError(errorCode);
			ErrorMessage = PayJoinReceiverHelper.GetMessage(errorCode);
		}
		public string ErrorCode { get; }
		public string ErrorMessage { get; }
		public string ReceiverMessage { get; }

		public PayJoinReceiverWellknownErrors? WellknownError
		{
			get;
		}

		private static string FormatMessage(string errorCode, string receiverMessage)
		{
			return $"{errorCode}: {PayJoinReceiverHelper.GetMessage(errorCode)}. (Receiver message: {receiverMessage})";
		}
	}
}