using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Primitives;

namespace ServIo
{
	public class DynamicActionDescriptor : IActionDescriptorChangeProvider
	{
		public CancellationTokenSource TokenSource { get; private set; } = new CancellationTokenSource();

		public IChangeToken GetChangeToken()
		{
			return new CancellationChangeToken(TokenSource.Token);
		}

		public void NotifyChange()
		{
			CancellationTokenSource oldTokenSource = TokenSource;
			TokenSource = new CancellationTokenSource();
			oldTokenSource.Cancel();
		}
	}
}
