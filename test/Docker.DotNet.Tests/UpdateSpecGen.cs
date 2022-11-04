using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.OpenApi.Readers;
using Xunit;

namespace Docker.DotNet.Tests;

public sealed class UpdateSpecGen : IAsyncLifetime, IDisposable
{
    private const string ModelDefinitionFilePath = "C:\\Sources\\GitHub\\Docker.DotNet\\tools\\specgen\\modeldefs.go";

    private const string OpenApiSpecFilePath = "C:\\Sources\\GitHub\\Docker.DotNet\\v1.41.yaml";

    // Contains the line number of each "modeldefs.go" API configuration.
    private readonly IList<RestMethod> _methodsModelDefinition = new List<RestMethod>();

    private readonly IList<RestMethod> _methodsOpenApiSpec = new List<RestMethod>();

    private readonly FileStream _logFile =
        new FileStream("C:/Temp/docker-api.log", FileMode.OpenOrCreate, FileAccess.Write);

    private readonly StreamWriter _logFileWriter;

    public UpdateSpecGen()
    {
        _logFileWriter = new StreamWriter(_logFile, Encoding.UTF8);
    }

    [Fact(Skip = "YES")]
    public Task Update()
    {
        IList<string> writeAtEnd = new List<string>();

        IDictionary<int, RestMethod> _methodsModelDefinitionSet = _methodsModelDefinition
            .Where(item => !item.IsResponse)
            .ToDictionary(item => item.GetHashCode(), item => item);

        IDictionary<int, RestMethod> _methodsOpenApiSpecSet =
            _methodsOpenApiSpec.ToDictionary(item => item.GetHashCode(), item => item);

        _logFileWriter.WriteLine(
            $"~{_methodsModelDefinitionSet.Count} Docker REST methods found in the Docker.DotNet configuration");
        _logFileWriter.WriteLine(
            $"~{_methodsOpenApiSpecSet.Count} Docker REST methods found in the OpenAPI specification");
        _logFileWriter.WriteLine();

        foreach (var (key, modelDefinition) in _methodsModelDefinitionSet.OrderBy(item => item.Value.Path))
        {
            try
            {
                var openApiSpec = _methodsOpenApiSpecSet[key];
                var foo = openApiSpec.Parameters.Except(modelDefinition.Parameters)
                    .Where(item => !"path".Equals(item.Location, StringComparison.OrdinalIgnoreCase)).ToList();
                var bar = modelDefinition.Parameters.Except(openApiSpec.Parameters).ToList();

                if (foo.Count == 0 && bar.Count == 0)
                {
                    continue;
                }

                _logFileWriter.WriteLine($"Diffs in {modelDefinition.Method} {modelDefinition.Path}");
                _logFileWriter.WriteLine($"Found additional parameters in the OpenAPI specification: {foo.Count}");

                if (foo.Count > 0)
                {
                    _logFileWriter.WriteLine(string.Join(Environment.NewLine, foo));
                }


                _logFileWriter.WriteLine(
                    $"Found additional parameters in the Docker.DotNet configuration: {bar.Count}");


                if (bar.Count > 0)
                {
                    _logFileWriter.WriteLine(string.Join(Environment.NewLine, bar));
                }

                _logFileWriter.WriteLine();
            }
            catch (Exception)
            {
                if (modelDefinition.Path.EndsWith("foo", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                writeAtEnd.Add(
                    $"{modelDefinition.Method} {modelDefinition.Path} not found in the OpenAPI specification.");
            }
        }

        foreach (var atEnd in writeAtEnd)
        {
            _logFileWriter.WriteLine(atEnd);
        }

        if (writeAtEnd.Any())
        {
            _logFileWriter.WriteLine();
        }

        foreach (var (key, openApiSpec) in _methodsOpenApiSpecSet.OrderBy(item => item.Value.Path))
        {
            try
            {
                _ = _methodsModelDefinitionSet[key];
            }
            catch (Exception)
            {
                _logFileWriter.WriteLine(
                    $"{openApiSpec.Method} {openApiSpec.Path} not found in the Docker.DotNet configuration.");
            }
        }

        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        return Task.WhenAll(ReadModelDefinitionFile(), ReadOpenApiSpecFile());
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _logFileWriter.Flush();
        _logFileWriter.Dispose();
        _logFile.Dispose();
    }

    private Task ReadModelDefinitionFile()
    {
        File.ReadAllLines(ModelDefinitionFilePath)
            .AsParallel()
            .Select(line => line.Trim())
            .Select((line, lineNumber) => new KeyValuePair<int, string>(lineNumber, line))
            .Where(item => !string.IsNullOrEmpty(item.Value))
            .Where(item => item.Value.StartsWith("//", StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Value.Count(character => '/'.Equals(character)) > 2)
            .Select(item =>
            {
                var segments1 = item.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var method = segments1.TakeLast(2).First();
                var path = segments1.TakeLast(2).Last();

                var parameters = File.ReadLines(ModelDefinitionFilePath)
                    .Skip(item.Key + 2)
                    .TakeWhile(line => !line.Contains('}') && !string.IsNullOrWhiteSpace(line))
                    .Select(line => line.Trim())
                    .Where(line => !line.StartsWith("//"))
                    .Where(line => !string.IsNullOrEmpty(line))
                    .Select(line =>
                    {
                        var indexOfComment = line.IndexOf("//", StringComparison.OrdinalIgnoreCase);
                        if (indexOfComment > -1)
                        {
                            line = line.Substring(0, indexOfComment);
                        }

                        var segments2 = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                        string name;

                        string type;

                        string location;

                        switch (segments2.Length)
                        {
                            case 3:
                                var segments3 = segments2.Skip(2).First().Split('"').Skip(1).First().Split(',');

                                if (segments3.Length >= 2)
                                {
                                    name = segments3.Skip(1).First();
                                    type = segments2.Skip(1).First();
                                    location = segments3.Skip(0).First();
                                }
                                else
                                {
                                    name = segments2.Skip(0).First();
                                    type = segments2.Skip(1).First();
                                    location = segments3.Skip(0).First();
                                }

                                break;
                            case 2:
                                name = segments2.Skip(0).First();
                                type = segments2.Skip(1).First();
                                location = string.Empty;
                                break;
                            default:
                                name = line;
                                type = string.Empty;
                                location = string.Empty;
                                break;
                        }

                        return new RestParameter(type, name, location);
                    })
                    .ToArray();

                return new RestMethod(method, path, parameters.OrderBy(i => i.Location).ThenBy(i => i.Name), item.Key, segments1.Any(i => i.Contains("response", StringComparison.OrdinalIgnoreCase)));
            })
            .ToList()
            .ForEach(_methodsModelDefinition.Add);

        return Task.CompletedTask;
    }

    private Task ReadOpenApiSpecFile()
    {
        using (var openApiSpecFileStream = new FileStream(OpenApiSpecFilePath, FileMode.Open, FileAccess.Read))
        {
            var openApiSpecReader = new OpenApiStreamReader();
            var openApiDocument = openApiSpecReader.Read(openApiSpecFileStream, out _);

            foreach (var path in openApiDocument.Paths)
            {
                foreach (var operation in path.Value.Operations)
                {
                    var parameters = operation.Value.Parameters.Select(parameter =>
                        new RestParameter(parameter.Schema.Type, parameter.Name, parameter.In.ToString()));
                    _methodsOpenApiSpec.Add(new RestMethod(operation.Key.ToString(), path.Key,
                        parameters.OrderBy(i => i.Location).ThenBy(i => i.Name)));
                }
            }
        }

        return Task.CompletedTask;
    }

    private readonly struct RestMethod
    {
        public RestMethod(string method, string path, IEnumerable<RestParameter> parameters, int line = 0, bool response = false)
        {
            var i = path.IndexOfAny(new[] { '{', '(' });
            var j = path.IndexOfAny(new[] { '}', ')' });

            if (i > -1 && j > -1)
            {
                path = path.Substring(0, i) + "*" + path.Substring(j + 1, path.Length - j - 1);
            }

            Method = method.ToUpperInvariant().Trim();
            Path = path.ToLowerInvariant().Trim();
            Parameters = parameters;
            Line = line;
            IsResponse = response;
        }

        public string Method { get; }

        public string Path { get; }

        public IEnumerable<RestParameter> Parameters { get; }

        public int Line { get; }

        public bool IsResponse { get; }

        public override int GetHashCode()
        {
            return HashCode.Combine(Method, Path);
        }
    }

    private readonly struct RestParameter
    {
        public RestParameter(string type, string name, string location)
        {
            Type = type.ToLowerInvariant().Trim();
            Name = name.ToLowerInvariant().Trim();
            Location = location.ToLowerInvariant().Trim();
        }

        public string Type { get; }

        public string Name { get; }

        public string Location { get; }

        public override int GetHashCode()
        {
            return Name.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            return Equals((RestParameter)obj);
        }

        public bool Equals(RestParameter obj)
        {
            return string.Equals(Name, obj.Name, StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString()
        {
            return "  " + Name + ":" + Type + ":" + Location;
        }
    }
}