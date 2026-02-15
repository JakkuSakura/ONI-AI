using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OniAiAssistantRuntime
{
    internal static class RuntimeJson
    {
        public static JObject ReadJsonBody(HttpListenerRequest request)
        {
            try
            {
                Encoding encoding = request?.ContentEncoding;
                if (encoding == null)
                {
                    return null;
                }

                using (var reader = new StreamReader(request.InputStream, encoding))
                {
                    string body = reader.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(body))
                    {
                        return null;
                    }

                    return JsonConvert.DeserializeObject<JObject>(body);
                }
            }
            catch
            {
                return null;
            }
        }

        public static void WriteJson(HttpListenerResponse response, int statusCode, JObject payload)
        {
            JObject safePayload = payload ?? new JObject { ["error"] = "invalid_payload" };
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(safePayload.ToString(Formatting.None));
                response.StatusCode = statusCode;
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength64 = bytes.Length;
                response.OutputStream.Write(bytes, 0, bytes.Length);
                response.OutputStream.Flush();
            }
            catch
            {
                Debug.LogWarning("[ONI-AI] Failed to write HTTP JSON response status=" + statusCode.ToString(CultureInfo.InvariantCulture));
            }
            finally
            {
                try
                {
                    response.OutputStream.Close();
                }
                catch
                {
                }
            }
        }
    }
}
