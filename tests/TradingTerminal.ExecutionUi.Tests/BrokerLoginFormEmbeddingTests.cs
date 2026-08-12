using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using TradingTerminal.App.Login.Forms;
using TradingTerminal.ExecutionUi;

namespace TradingTerminal.ExecutionUi.Tests;

public sealed class BrokerLoginFormEmbeddingTests
{
    [Fact]
    public void ExecutionView_UsesSharedLoginControlsForBrokerFormViewModels()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = Application.Current ?? new Application();
                application.Resources["App.SectionBar"] = new Style(typeof(Border));
                using var view = new ExecutionConsoleView();
                Assert.True(view.Resources.Contains(new DataTemplateKey(typeof(AlpacaLoginFormViewModel))));
                Assert.True(view.Resources.Contains(new DataTemplateKey(typeof(CTraderLoginFormViewModel))));
                Assert.True(view.Resources.Contains(new DataTemplateKey(typeof(IbLoginFormViewModel))));

                application.Resources["BooleanToVisibilityConverter"] = view.Resources["BooleanToVisibilityConverter"];
                application.Resources["StringToVisibilityConverter"] = view.Resources["StringToVisibilityConverter"];
                application.Resources["InverseBooleanToVisibilityConverter"] = view.Resources["InverseBooleanToVisibilityConverter"];

                AssertTemplate<AlpacaLoginFormViewModel, AlpacaLoginForm>(view);
                AssertTemplate<CTraderLoginFormViewModel, CTraderLoginForm>(view);
                AssertTemplate<IbLoginFormViewModel, IbLoginForm>(view);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The STA Login-form render check timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void AssertTemplate<TViewModel, TControl>(ExecutionConsoleView view)
        where TControl : UserControl
    {
        var template = Assert.IsType<DataTemplate>(view.Resources[new DataTemplateKey(typeof(TViewModel))]);
        Assert.IsType<TControl>(template.LoadContent());
    }
}
