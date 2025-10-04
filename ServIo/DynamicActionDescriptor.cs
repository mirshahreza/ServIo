using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Primitives;

namespace ServIo
{
	public class DynamicActionDescriptor : IActionDescriptorChangeProvider
	{
		private readonly object _lock = new object();
		public CancellationTokenSource TokenSource { get; private set; } = new CancellationTokenSource();

		public IChangeToken GetChangeToken()
		{
			return new CancellationChangeToken(TokenSource.Token);
		}

		public void NotifyChange()
		{
			lock (_lock)
			{
				CancellationTokenSource oldTokenSource = TokenSource;
				TokenSource = new CancellationTokenSource();
				oldTokenSource.Cancel();
			}
		}
	}
}
