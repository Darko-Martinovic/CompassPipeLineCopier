using System.Text;
using PipelineCopier;

// The report marks each pipeline and index with ✓, ○ or ✗
Console.OutputEncoding = Encoding.UTF8;

return await App.RunAsync(args, Console.Out);
