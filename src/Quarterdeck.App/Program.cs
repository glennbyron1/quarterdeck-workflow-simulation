using Quarterdeck.App;

var builder = QuarterdeckRazorHostFactory.CreateBuilder(args);
SimulationHostComposition.Configure(builder);
var app = QuarterdeckRazorHostFactory.Build(builder);
app.Run();

public partial class Program;
