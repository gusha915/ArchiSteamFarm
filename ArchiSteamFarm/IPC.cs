﻿//     _                _      _  ____   _                           _____
//    / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
//   / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
//  / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
// /_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|
// 
//  Copyright 2015-2018 Łukasz "JustArchi" Domeradzki
//  Contact: JustArchi@JustArchi.net
// 
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
// 
//  http://www.apache.org/licenses/LICENSE-2.0
//      
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using ArchiSteamFarm.Localization;
using Newtonsoft.Json;

namespace ArchiSteamFarm {
	internal static class IPC {
		internal static bool IsRunning => IsHandlingRequests || IsListening;

		private static bool IsListening {
			get {
				try {
					return HttpListener?.IsListening == true;
				} catch (ObjectDisposedException) {
					// HttpListener can dispose itself on error
					return false;
				}
			}
		}

		private static HttpListener HttpListener;
		private static bool IsHandlingRequests;

		internal static void Start(string host, ushort port) {
			if (string.IsNullOrEmpty(host) || (port == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(host) + " || " + nameof(port));
				return;
			}

			if (!HttpListener.IsSupported) {
				ASF.ArchiLogger.LogGenericError(string.Format(Strings.WarningFailedWithError, "!HttpListener.IsSupported"));
				return;
			}

			switch (host) {
				case "0.0.0.0":
				case "::":
					// Silently map INADDR_ANY to match HttpListener expectations
					host = "*";
					break;
			}

			string url = "http://" + host + ":" + port + "/";
			HttpListener = new HttpListener { IgnoreWriteExceptions = true };

			try {
				ASF.ArchiLogger.LogGenericInfo(string.Format(Strings.IPCStarting, url));

				HttpListener.Prefixes.Add(url);
				HttpListener.Start();
			} catch (Exception e) {
				// HttpListener can dispose itself on error, so don't keep it around
				HttpListener = null;
				ASF.ArchiLogger.LogGenericException(e);
				return;
			}

			Utilities.StartBackgroundFunction(HandleRequests);
			ASF.ArchiLogger.LogGenericInfo(Strings.IPCReady);
		}

		internal static void Stop() {
			if (!IsListening) {
				return;
			}

			// We must set HttpListener to null before stopping it, so HandleRequests() knows that exception is expected
			HttpListener httpListener = HttpListener;
			HttpListener = null;

			httpListener.Stop();
		}

		private static async Task<bool> HandleApi(HttpListenerRequest request, HttpListenerResponse response, string[] arguments, byte argumentsIndex) {
			if ((request == null) || (response == null) || (arguments == null) || (argumentsIndex == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response) + " || " + nameof(arguments) + " || " + nameof(argumentsIndex));
				return false;
			}

			if (arguments.Length <= argumentsIndex) {
				return false;
			}

			switch (arguments[argumentsIndex]) {
				case "Bot/":
					return await HandleApiBot(request, response, arguments, ++argumentsIndex).ConfigureAwait(false);
				case "Command":
				case "Command/":
					return await HandleApiCommand(request, response, arguments, ++argumentsIndex).ConfigureAwait(false);
				case "Structure/":
					return await HandleApiStructure(request, response, arguments, ++argumentsIndex).ConfigureAwait(false);
				case "Type/":
					return await HandleApiType(request, response, arguments, ++argumentsIndex).ConfigureAwait(false);
				default:
					return false;
			}
		}

		private static async Task<bool> HandleApiBot(HttpListenerRequest request, HttpListenerResponse response, string[] arguments, byte argumentsIndex) {
			if ((request == null) || (response == null) || (arguments == null) || (argumentsIndex == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response) + " || " + nameof(arguments) + " || " + nameof(argumentsIndex));
				return false;
			}

			switch (request.HttpMethod) {
				case HttpMethods.Delete:
					return await HandleApiBotDelete(request, response, arguments, argumentsIndex).ConfigureAwait(false);
				case HttpMethods.Get:
					return await HandleApiBotGet(request, response, arguments, argumentsIndex).ConfigureAwait(false);
				case HttpMethods.Post:
					return await HandleApiBotPost(request, response, arguments, argumentsIndex).ConfigureAwait(false);
				default:
					await ResponseStatusCode(request, response, HttpStatusCode.MethodNotAllowed).ConfigureAwait(false);
					return true;
			}
		}

		private static async Task<bool> HandleApiBotDelete(HttpListenerRequest request, HttpListenerResponse response, string[] arguments, byte argumentsIndex) {
			if ((request == null) || (response == null) || (arguments == null) || (argumentsIndex == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response) + " || " + nameof(arguments) + " || " + nameof(argumentsIndex));
				return false;
			}

			if (arguments.Length <= argumentsIndex) {
				return false;
			}

			string botNames = WebUtility.UrlDecode(arguments[argumentsIndex]);

			HashSet<Bot> bots = Bot.GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				await ResponseJsonObject(request, response, new GenericResponse(false, string.Format(Strings.BotNotFound, botNames)), HttpStatusCode.BadRequest).ConfigureAwait(false);
				return true;
			}

			IEnumerable<Task<bool>> tasks = bots.Select(bot => bot.DeleteAllRelatedFiles());
			ICollection<bool> results;

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<bool>(bots.Count);
					foreach (Task<bool> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			if (results.Any(result => !result)) {
				await ResponseJsonObject(request, response, new GenericResponse(false, "Removing one or more files failed, check ASF log for details"), HttpStatusCode.BadRequest).ConfigureAwait(false);
				return true;
			}

			await ResponseJsonObject(request, response, new GenericResponse(true, "OK")).ConfigureAwait(false);
			return true;
		}

		private static async Task<bool> HandleApiBotGet(HttpListenerRequest request, HttpListenerResponse response, string[] arguments, byte argumentsIndex) {
			if ((request == null) || (response == null) || (arguments == null) || (argumentsIndex == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response) + " || " + nameof(arguments) + " || " + nameof(argumentsIndex));
				return false;
			}

			if (arguments.Length <= argumentsIndex) {
				return false;
			}

			string botNames = WebUtility.UrlDecode(arguments[argumentsIndex]);

			HashSet<Bot> bots = Bot.GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				await ResponseJsonObject(request, response, new GenericResponse(false, string.Format(Strings.BotNotFound, botNames)), HttpStatusCode.BadRequest).ConfigureAwait(false);
				return true;
			}

			await ResponseJsonObject(request, response, new GenericResponse(true, "OK", bots)).ConfigureAwait(false);
			return true;
		}

		private static async Task<bool> HandleApiBotPost(HttpListenerRequest request, HttpListenerResponse response, string[] arguments, byte argumentsIndex) {
			if ((request == null) || (response == null) || (arguments == null) || (argumentsIndex == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response) + " || " + nameof(arguments) + " || " + nameof(argumentsIndex));
				return false;
			}

			if (arguments.Length <= argumentsIndex) {
				return false;
			}

			const string requiredContentType = "application/json";

			if (request.ContentType != requiredContentType) {
				await ResponseJsonObject(request, response, new GenericResponse(false, nameof(request.ContentType) + " must be declared as " + requiredContentType), HttpStatusCode.NotAcceptable).ConfigureAwait(false);
				return true;
			}

			string body;
			using (StreamReader reader = new StreamReader(request.InputStream)) {
				body = await reader.ReadToEndAsync().ConfigureAwait(false);
			}

			if (string.IsNullOrEmpty(body)) {
				await ResponseJsonObject(request, response, new GenericResponse(false, string.Format(Strings.ErrorIsEmpty, nameof(body))), HttpStatusCode.BadRequest).ConfigureAwait(false);
				return true;
			}

			BotRequest botRequest;

			try {
				botRequest = JsonConvert.DeserializeObject<BotRequest>(body);
			} catch (Exception e) {
				await ResponseJsonObject(request, response, new GenericResponse(false, string.Format(Strings.ErrorParsingObject, nameof(botRequest)) + Environment.NewLine + e), HttpStatusCode.BadRequest).ConfigureAwait(false);
				return true;
			}

			string botName = WebUtility.UrlDecode(arguments[argumentsIndex]);

			if (botRequest.KeepSensitiveDetails && Bot.Bots.TryGetValue(botName, out Bot bot)) {
				if (string.IsNullOrEmpty(botRequest.BotConfig.SteamLogin)) {
					botRequest.BotConfig.SteamLogin = bot.BotConfig.SteamLogin;
				}

				if (string.IsNullOrEmpty(botRequest.BotConfig.SteamParentalPIN)) {
					botRequest.BotConfig.SteamParentalPIN = bot.BotConfig.SteamParentalPIN;
				}

				if (string.IsNullOrEmpty(botRequest.BotConfig.SteamPassword)) {
					botRequest.BotConfig.SteamPassword = bot.BotConfig.SteamPassword;
				}
			}

			string filePath = Path.Combine(SharedInfo.ConfigDirectory, botName + ".json");

			if (!await BotConfig.Write(filePath, botRequest.BotConfig).ConfigureAwait(false)) {
				await ResponseJsonObject(request, response, new GenericResponse(false, "Writing bot config failed, check ASF log for details"), HttpStatusCode.BadRequest).ConfigureAwait(false);
				return true;
			}

			await ResponseJsonObject(request, response, new GenericResponse(true, "OK")).ConfigureAwait(false);
			return true;
		}

		private static async Task<bool> HandleApiCommand(HttpListenerRequest request, HttpListenerResponse response, string[] arguments, byte argumentsIndex) {
			if ((request == null) || (response == null) || (arguments == null) || (argumentsIndex == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response) + " || " + nameof(arguments) + " || " + nameof(argumentsIndex));
				return false;
			}

			if (Program.GlobalConfig.SteamOwnerID == 0) {
				await ResponseJsonObject(request, response, new GenericResponse(false, string.Format(Strings.ErrorIsInvalid, nameof(Program.GlobalConfig.SteamOwnerID))), HttpStatusCode.Forbidden).ConfigureAwait(false);
				return true;
			}

			switch (request.HttpMethod) {
				case HttpMethods.Get:
					return await HandleApiCommandGeneric(request, response, arguments, argumentsIndex).ConfigureAwait(false);
				case HttpMethods.Post:
					return await HandleApiCommandGeneric(request, response, arguments, argumentsIndex).ConfigureAwait(false);
				default:
					await ResponseStatusCode(request, response, HttpStatusCode.MethodNotAllowed).ConfigureAwait(false);
					return true;
			}
		}

		private static async Task<bool> HandleApiCommandGeneric(HttpListenerRequest request, HttpListenerResponse response, string[] arguments, byte argumentsIndex) {
			if ((request == null) || (response == null) || (arguments == null) || (argumentsIndex == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response) + " || " + nameof(arguments) + " || " + nameof(argumentsIndex));
				return false;
			}

			if (arguments.Length <= argumentsIndex) {
				return false;
			}

			string command = WebUtility.UrlDecode(arguments[argumentsIndex]);
			if (string.IsNullOrEmpty(command)) {
				await ResponseJsonObject(request, response, new GenericResponse(false, string.Format(Strings.ErrorIsEmpty, nameof(command))), HttpStatusCode.BadRequest).ConfigureAwait(false);
				return true;
			}

			Bot targetBot = Bot.Bots.OrderBy(bot => bot.Key).Select(bot => bot.Value).FirstOrDefault();
			if (targetBot == null) {
				await ResponseJsonObject(request, response, new GenericResponse(false, Strings.ErrorNoBotsDefined), HttpStatusCode.BadRequest).ConfigureAwait(false);
				return true;
			}

			if (command[0] != '!') {
				command = "!" + command;
			}

			string content = await targetBot.Response(Program.GlobalConfig.SteamOwnerID, command).ConfigureAwait(false);

			await ResponseJsonObject(request, response, new GenericResponse(true, "OK", content)).ConfigureAwait(false);
			return true;
		}

		private static async Task<bool> HandleApiStructure(HttpListenerRequest request, HttpListenerResponse response, string[] arguments, byte argumentsIndex) {
			if ((request == null) || (response == null) || (arguments == null) || (argumentsIndex == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response) + " || " + nameof(arguments) + " || " + nameof(argumentsIndex));
				return false;
			}

			switch (request.HttpMethod) {
				case HttpMethods.Get:
					return await HandleApiStructureGet(request, response, arguments, argumentsIndex).ConfigureAwait(false);
				default:
					await ResponseStatusCode(request, response, HttpStatusCode.MethodNotAllowed).ConfigureAwait(false);
					return true;
			}
		}

		private static async Task<bool> HandleApiStructureGet(HttpListenerRequest request, HttpListenerResponse response, string[] arguments, byte argumentsIndex) {
			if ((request == null) || (response == null) || (arguments == null) || (argumentsIndex == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response) + " || " + nameof(arguments) + " || " + nameof(argumentsIndex));
				return false;
			}

			if (arguments.Length <= argumentsIndex) {
				return false;
			}

			string argument = WebUtility.UrlDecode(arguments[argumentsIndex]);
			Type targetType = Type.GetType(argument);

			if (targetType == null) {
				await ResponseJsonObject(request, response, new GenericResponse(false, string.Format(Strings.ErrorIsInvalid, nameof(argument))), HttpStatusCode.BadRequest).ConfigureAwait(false);
				return true;
			}

			object obj;

			try {
				obj = Activator.CreateInstance(targetType, true);
			} catch (Exception e) {
				await ResponseJsonObject(request, response, new GenericResponse(false, string.Format(Strings.ErrorParsingObject, targetType) + Environment.NewLine + e), HttpStatusCode.BadRequest).ConfigureAwait(false);
				return true;
			}

			if (obj == null) {
				await ResponseJsonObject(request, response, new GenericResponse(false, string.Format(Strings.ErrorParsingObject, targetType)), HttpStatusCode.BadRequest).ConfigureAwait(false);
				return true;
			}

			await ResponseJsonObject(request, response, new GenericResponse(true, "OK", obj)).ConfigureAwait(false);
			return true;
		}

		private static async Task<bool> HandleApiType(HttpListenerRequest request, HttpListenerResponse response, string[] arguments, byte argumentsIndex) {
			if ((request == null) || (response == null) || (arguments == null) || (argumentsIndex == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response) + " || " + nameof(arguments) + " || " + nameof(argumentsIndex));
				return false;
			}

			switch (request.HttpMethod) {
				case HttpMethods.Get:
					return await HandleApiTypeGet(request, response, arguments, argumentsIndex).ConfigureAwait(false);
				default:
					await ResponseStatusCode(request, response, HttpStatusCode.MethodNotAllowed).ConfigureAwait(false);
					return true;
			}
		}

		private static async Task<bool> HandleApiTypeGet(HttpListenerRequest request, HttpListenerResponse response, string[] arguments, byte argumentsIndex) {
			if ((request == null) || (response == null) || (arguments == null) || (argumentsIndex == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response) + " || " + nameof(arguments) + " || " + nameof(argumentsIndex));
				return false;
			}

			if (arguments.Length <= argumentsIndex) {
				return false;
			}

			string argument = WebUtility.UrlDecode(arguments[argumentsIndex]);
			Type targetType = Type.GetType(argument);

			if (targetType == null) {
				await ResponseJsonObject(request, response, new GenericResponse(false, string.Format(Strings.ErrorIsInvalid, nameof(argument))), HttpStatusCode.BadRequest).ConfigureAwait(false);
				return true;
			}

			Dictionary<string, string> result = new Dictionary<string, string>();

			if (targetType.IsClass) {
				foreach (FieldInfo field in targetType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Where(field => !field.IsPrivate)) {
					result[field.Name] = field.FieldType.GetUnifiedName();
				}

				foreach (PropertyInfo property in targetType.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Where(property => property.CanRead)) {
					result[property.Name] = property.PropertyType.GetUnifiedName();
				}
			} else if (targetType.IsEnum) {
				Type enumType = Enum.GetUnderlyingType(targetType);

				foreach (object value in Enum.GetValues(targetType)) {
					result[value.ToString()] = Convert.ChangeType(value, enumType).ToString();
				}
			}

			await ResponseJsonObject(request, response, new GenericResponse(true, "OK", result)).ConfigureAwait(false);
			return true;
		}

		private static async Task<bool> HandleAuthenticatedRequest(HttpListenerRequest request, HttpListenerResponse response) {
			if ((request == null) || (response == null)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response));
				return false;
			}

			if (request.Url.Segments.Length < 2) {
				return await HandleMainPage(request, response).ConfigureAwait(false);
			}

			switch (request.Url.Segments[1]) {
				case "Api/":
					return await HandleApi(request, response, request.Url.Segments, 2).ConfigureAwait(false);
				default:
					return false;
			}
		}

		private static async Task<bool> HandleMainPage(HttpListenerRequest request, HttpListenerResponse response) {
			if ((request == null) || (response == null)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response));
				return false;
			}

			// In the future we'll probably have some friendly admin panel here, for now this is 501
			await ResponseStatusCode(request, response, HttpStatusCode.NotImplemented).ConfigureAwait(false);
			return true;
		}

		private static async Task HandleRequest(HttpListenerContext context) {
			if (context == null) {
				ASF.ArchiLogger.LogNullError(nameof(context));
				return;
			}

			try {
				if (!string.IsNullOrEmpty(Program.GlobalConfig.IPCPassword)) {
					string password = context.Request.Headers.Get("Authentication");
					if (string.IsNullOrEmpty(password)) {
						password = context.Request.QueryString.Get("password");
					}

					if (password != Program.GlobalConfig.IPCPassword) {
						await ResponseStatusCode(context.Request, context.Response, HttpStatusCode.Unauthorized).ConfigureAwait(false);
						return;
					}
				}

				if (!await HandleAuthenticatedRequest(context.Request, context.Response).ConfigureAwait(false)) {
					await ResponseStatusCode(context.Request, context.Response, HttpStatusCode.NotFound).ConfigureAwait(false);
				}
			} finally {
				context.Response.Close();
			}
		}

		private static async Task HandleRequests() {
			if (IsHandlingRequests) {
				return;
			}

			IsHandlingRequests = true;

			try {
				while (IsListening) {
					Task<HttpListenerContext> task = HttpListener?.GetContextAsync();
					if (task == null) {
						return;
					}

					HttpListenerContext context;

					try {
						context = await task.ConfigureAwait(false);
					} catch (HttpListenerException e) {
						// If HttpListener is null then we're stopping HttpListener, so this exception is expected, ignore it
						if (HttpListener == null) {
							return;
						}

						// Otherwise this is an error, and HttpListener can dispose itself in this situation, so don't keep it around
						HttpListener = null;
						ASF.ArchiLogger.LogGenericException(e);
						return;
					}

					Utilities.StartBackgroundFunction(() => HandleRequest(context), false);
				}
			} finally {
				IsHandlingRequests = false;
			}
		}

		private static async Task ResponseBase(HttpListenerRequest request, HttpListenerResponse response, byte[] content, HttpStatusCode statusCode = HttpStatusCode.OK) {
			if ((request == null) || (response == null) || (content == null) || (content.Length == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response) + " || " + nameof(content));
				return;
			}

			try {
				if (response.StatusCode != (ushort) statusCode) {
					response.StatusCode = (ushort) statusCode;
				}

				response.AppendHeader("Access-Control-Allow-Origin", "*");

				string acceptEncoding = request.Headers["Accept-Encoding"];

				if (!string.IsNullOrEmpty(acceptEncoding)) {
					if (acceptEncoding.Contains("gzip")) {
						response.AddHeader("Content-Encoding", "gzip");
						using (MemoryStream ms = new MemoryStream()) {
							using (GZipStream stream = new GZipStream(ms, CompressionMode.Compress)) {
								await stream.WriteAsync(content, 0, content.Length).ConfigureAwait(false);
							}

							content = ms.ToArray();
						}
					} else if (acceptEncoding.Contains("deflate")) {
						response.AddHeader("Content-Encoding", "deflate");
						using (MemoryStream ms = new MemoryStream()) {
							using (DeflateStream stream = new DeflateStream(ms, CompressionMode.Compress)) {
								await stream.WriteAsync(content, 0, content.Length).ConfigureAwait(false);
							}

							content = ms.ToArray();
						}
					}
				}

				response.ContentLength64 = content.Length;
				await response.OutputStream.WriteAsync(content, 0, content.Length).ConfigureAwait(false);
			} catch (ObjectDisposedException) {
				// Ignored, request is no longer valid
			}
		}

		private static async Task ResponseJson(HttpListenerRequest request, HttpListenerResponse response, string json, HttpStatusCode statusCode = HttpStatusCode.OK) {
			if ((request == null) || (response == null) || string.IsNullOrEmpty(json)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response) + " || " + nameof(json));
				return;
			}

			await ResponseString(request, response, json, "text/json", statusCode).ConfigureAwait(false);
		}

		private static async Task ResponseJsonObject(HttpListenerRequest request, HttpListenerResponse response, object obj, HttpStatusCode statusCode = HttpStatusCode.OK) {
			if ((request == null) || (response == null) || (obj == null)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response) + " || " + nameof(obj));
				return;
			}

			await ResponseJson(request, response, JsonConvert.SerializeObject(obj), statusCode).ConfigureAwait(false);
		}

		private static async Task ResponseStatusCode(HttpListenerRequest request, HttpListenerResponse response, HttpStatusCode statusCode) {
			if ((request == null) || (response == null)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response));
				return;
			}

			string text = (ushort) statusCode + " - " + statusCode;
			await ResponseText(request, response, text, statusCode).ConfigureAwait(false);
		}

		private static async Task ResponseString(HttpListenerRequest request, HttpListenerResponse response, string text, string textType, HttpStatusCode statusCode) {
			if ((request == null) || (response == null) || string.IsNullOrEmpty(text) || string.IsNullOrEmpty(textType)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response) + " || " + nameof(text) + " || " + nameof(textType));
				return;
			}

			try {
				if (response.ContentEncoding == null) {
					response.ContentEncoding = Encoding.UTF8;
				}

				response.ContentType = textType + "; charset=" + response.ContentEncoding.WebName;

				byte[] content = response.ContentEncoding.GetBytes(text + Environment.NewLine);
				await ResponseBase(request, response, content, statusCode).ConfigureAwait(false);
			} catch (ObjectDisposedException) {
				// Ignored, request is no longer valid
			}
		}

		private static async Task ResponseText(HttpListenerRequest request, HttpListenerResponse response, string text, HttpStatusCode statusCode = HttpStatusCode.OK) {
			if ((request == null) || (response == null) || string.IsNullOrEmpty(text)) {
				ASF.ArchiLogger.LogNullError(nameof(request) + " || " + nameof(response) + " || " + nameof(text));
				return;
			}

			await ResponseString(request, response, text, "text/plain", statusCode).ConfigureAwait(false);
		}

		[SuppressMessage("ReSharper", "ClassCannotBeInstantiated")]
		private sealed class BotRequest {
#pragma warning disable 649
			[JsonProperty(Required = Required.Always)]
			internal readonly BotConfig BotConfig;
#pragma warning restore 649

			[JsonProperty(Required = Required.DisallowNull)]
			internal readonly bool KeepSensitiveDetails = true;

			// Deserialized from JSON
			private BotRequest() { }
		}

		private sealed class GenericResponse {
			[JsonProperty]
			internal readonly string Message;

			[JsonProperty]
			internal readonly object Result;

			[JsonProperty]
			internal readonly bool Success;

			internal GenericResponse(bool success, string message = null, object result = null) {
				Success = success;
				Message = message;
				Result = result;
			}
		}

		private static class HttpMethods {
			internal const string Delete = "DELETE";
			internal const string Get = "GET";
			internal const string Post = "POST";
		}
	}
}