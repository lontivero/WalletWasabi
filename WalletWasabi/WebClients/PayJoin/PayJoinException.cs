using System;

namespace WalletWasabi.WebClients.PayJoin
{
	public class PayJoinException : Exception
	{
		public PayJoinException(string message) : base(message)
		{
		}
	}
}
