// SPDX-License-Identifier: GPL-3.0-or-later
// 
// PCCCComm - PCCC Communication Library for .NET
// Copyright (c) 2026 Ketut Kumajaya
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Xml;
using PCCCComm.Pccc;

namespace PCCCComm.Core
{
    /// <summary>
    /// Manages ML1400 data file list from filelist.xml (local file or HTTP).
    /// </summary>
    public static class Ml1400FileList
    {
        private static DataFileDetails[]? _cachedList;

        /// <summary>
        /// Gets the file list. Loads from local file if path provided and exists,
        /// otherwise fetches from PLC via HTTP if remote host given and EIP,
        /// otherwise returns embedded default list.
        /// </summary>
        public static DataFileDetails[] GetFileList(string? localFilePath = null, string? remoteHost = null, int httpPort = 80, string? username = null, string? password = null)
        {
            // If we have a cached list and no request to refresh, return it
            if (_cachedList != null && string.IsNullOrEmpty(localFilePath) && string.IsNullOrEmpty(remoteHost))
                return _cachedList;

            // Priority 1: load from local file
            if (!string.IsNullOrEmpty(localFilePath) && File.Exists(localFilePath))
            {
                try
                {
                    _cachedList = LoadFromXmlFile(localFilePath!);
                    return _cachedList;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load filelist.xml from {localFilePath}: {ex.Message}");
                    // Fall through to next option
                }
            }

            // Priority 2: fetch from PLC via HTTP (only if remote host provided)
            if (!string.IsNullOrEmpty(remoteHost))
            {
                try
                {
                    _cachedList = FetchFromHttp(remoteHost!, httpPort, username, password);
                    return _cachedList;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to fetch filelist.xml from {remoteHost}:{httpPort}: {ex.Message}");
                    // Fall through to default
                }
            }

            // Priority 3: embedded default
            _cachedList ??= GetDefaultList();
            return _cachedList;
        }

        /// <summary>
        /// Parses filelist.xml from a local file.
        /// </summary>
        private static DataFileDetails[] LoadFromXmlFile(string filePath)
        {
            var doc = new XmlDocument();
            doc.Load(filePath);
            return ParseXml(doc);
        }

        /// <summary>
        /// Fetches filelist.xml from ML1400 web server over HTTP.
        /// </summary>
        private static DataFileDetails[] FetchFromHttp(string host, int port, string? username, string? password)
        {
            string url = $"http://{host}:{port}/filelist.xml";

            var handler = new HttpClientHandler
            {
                Credentials = !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)
                    ? new System.Net.NetworkCredential(username, password)
                    : null,
                PreAuthenticate = true,
                AllowAutoRedirect = false,
            };

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            string xml = client.GetStringAsync(url).GetAwaiter().GetResult();

            var doc = new XmlDocument();
            doc.LoadXml(xml);
            return ParseXml(doc);
        }

        /// <summary>
        /// Parses XML document into DataFileDetails[].
        /// </summary>
        private static DataFileDetails[] ParseXml(XmlDocument doc)
        {
            var result = new List<DataFileDetails>();
            var nodes = doc.SelectNodes("/C/CD");
            if (nodes == null) return result.ToArray();

            foreach (XmlNode cd in nodes)
            {
                if (!byte.TryParse(cd["T2"]?.InnerText, out byte typeCode)) continue;
                if (!int.TryParse(cd["T3"]?.InnerText, out int fileNum)) continue;
                if (!int.TryParse(cd["T4"]?.InnerText, out int elemCount)) continue;

                var ftEnum = (PCCCConstants.SlcFileTypeCode)typeCode;
                string ftStr = PCCCConstants.SlcFileTypeInfo.GetTypeName(ftEnum);
                if (ftStr == "??") continue;

                result.Add(new DataFileDetails
                {
                    FileNumber = fileNum,
                    FileType = ftStr,
                    NumberOfElements = elemCount
                });
            }
            return result.ToArray();
        }

        /// <summary>
        /// Embedded default list (68 files from typical ML1400 program).
        /// </summary>
        private static DataFileDetails[] GetDefaultList()
        {
            return new DataFileDetails[]
            {
                new DataFileDetails { FileType = "O",  FileNumber = 0,  NumberOfElements = 9   },
                new DataFileDetails { FileType = "I",  FileNumber = 1,  NumberOfElements = 41  },
                new DataFileDetails { FileType = "S",  FileNumber = 2,  NumberOfElements = 66  },
                new DataFileDetails { FileType = "B",  FileNumber = 3,  NumberOfElements = 9   },
                new DataFileDetails { FileType = "T",  FileNumber = 4,  NumberOfElements = 76  },
                new DataFileDetails { FileType = "C",  FileNumber = 5,  NumberOfElements = 4   },
                new DataFileDetails { FileType = "R",  FileNumber = 6,  NumberOfElements = 1   },
                new DataFileDetails { FileType = "N",  FileNumber = 7,  NumberOfElements = 17  },
                new DataFileDetails { FileType = "F",  FileNumber = 8,  NumberOfElements = 202 },
                new DataFileDetails { FileType = "B",  FileNumber = 9,  NumberOfElements = 21  },
                new DataFileDetails { FileType = "B",  FileNumber = 10, NumberOfElements = 3   },
                new DataFileDetails { FileType = "B",  FileNumber = 11, NumberOfElements = 4   },
                new DataFileDetails { FileType = "N",  FileNumber = 12, NumberOfElements = 24  },
                new DataFileDetails { FileType = "F",  FileNumber = 13, NumberOfElements = 10  },
                new DataFileDetails { FileType = "N",  FileNumber = 14, NumberOfElements = 2   },
                new DataFileDetails { FileType = "N",  FileNumber = 15, NumberOfElements = 2   },
                new DataFileDetails { FileType = "B",  FileNumber = 16, NumberOfElements = 29  },
                new DataFileDetails { FileType = "N",  FileNumber = 17, NumberOfElements = 27  },
                new DataFileDetails { FileType = "F",  FileNumber = 18, NumberOfElements = 214 },
                new DataFileDetails { FileType = "N",  FileNumber = 19, NumberOfElements = 6   },
                new DataFileDetails { FileType = "L",  FileNumber = 20, NumberOfElements = 48  },
                new DataFileDetails { FileType = "ST", FileNumber = 21, NumberOfElements = 1   },
                new DataFileDetails { FileType = "T",  FileNumber = 25, NumberOfElements = 243 },
                new DataFileDetails { FileType = "N",  FileNumber = 26, NumberOfElements = 71  },
                new DataFileDetails { FileType = "B",  FileNumber = 27, NumberOfElements = 3   },
                new DataFileDetails { FileType = "L",  FileNumber = 28, NumberOfElements = 6   },
                new DataFileDetails { FileType = "F",  FileNumber = 29, NumberOfElements = 7   },
                new DataFileDetails { FileType = "F",  FileNumber = 30, NumberOfElements = 90  },
                new DataFileDetails { FileType = "T",  FileNumber = 35, NumberOfElements = 4   },
                new DataFileDetails { FileType = "N",  FileNumber = 36, NumberOfElements = 2   },
                new DataFileDetails { FileType = "F",  FileNumber = 37, NumberOfElements = 15  },
                new DataFileDetails { FileType = "L",  FileNumber = 38, NumberOfElements = 8   },
                new DataFileDetails { FileType = "L",  FileNumber = 60, NumberOfElements = 48  },
                new DataFileDetails { FileType = "L",  FileNumber = 61, NumberOfElements = 8   },
                new DataFileDetails { FileType = "F",  FileNumber = 70, NumberOfElements = 90  },
                new DataFileDetails { FileType = "F",  FileNumber = 71, NumberOfElements = 15  },
                new DataFileDetails { FileType = "L",  FileNumber = 80, NumberOfElements = 48  },
                new DataFileDetails { FileType = "L",  FileNumber = 81, NumberOfElements = 8   },
                new DataFileDetails { FileType = "F",  FileNumber = 90, NumberOfElements = 90  },
                new DataFileDetails { FileType = "F",  FileNumber = 91, NumberOfElements = 15  },
                new DataFileDetails { FileType = "L",  FileNumber = 100, NumberOfElements = 120 },
                new DataFileDetails { FileType = "L",  FileNumber = 101, NumberOfElements = 120 },
                new DataFileDetails { FileType = "L",  FileNumber = 102, NumberOfElements = 120 },
                new DataFileDetails { FileType = "L",  FileNumber = 103, NumberOfElements = 120 },
                new DataFileDetails { FileType = "L",  FileNumber = 104, NumberOfElements = 120 },
                new DataFileDetails { FileType = "L",  FileNumber = 105, NumberOfElements = 120 },
                new DataFileDetails { FileType = "L",  FileNumber = 106, NumberOfElements = 6   },
                new DataFileDetails { FileType = "F",  FileNumber = 116, NumberOfElements = 17  },
                new DataFileDetails { FileType = "N",  FileNumber = 120, NumberOfElements = 22  },
                new DataFileDetails { FileType = "N",  FileNumber = 121, NumberOfElements = 8   },
                new DataFileDetails { FileType = "F",  FileNumber = 122, NumberOfElements = 204 },
                new DataFileDetails { FileType = "F",  FileNumber = 123, NumberOfElements = 256 },
                new DataFileDetails { FileType = "F",  FileNumber = 124, NumberOfElements = 256 },
                new DataFileDetails { FileType = "F",  FileNumber = 125, NumberOfElements = 11  },
                new DataFileDetails { FileType = "B",  FileNumber = 127, NumberOfElements = 1   },
                new DataFileDetails { FileType = "L",  FileNumber = 128, NumberOfElements = 6   },
                new DataFileDetails { FileType = "L",  FileNumber = 129, NumberOfElements = 2   },
                new DataFileDetails { FileType = "L",  FileNumber = 130, NumberOfElements = 18  },
                new DataFileDetails { FileType = "L",  FileNumber = 131, NumberOfElements = 5   },
                new DataFileDetails { FileType = "N",  FileNumber = 210, NumberOfElements = 255 },
                new DataFileDetails { FileType = "N",  FileNumber = 211, NumberOfElements = 255 },
                new DataFileDetails { FileType = "N",  FileNumber = 212, NumberOfElements = 255 },
                new DataFileDetails { FileType = "N",  FileNumber = 213, NumberOfElements = 255 },
                new DataFileDetails { FileType = "N",  FileNumber = 214, NumberOfElements = 255 },
                new DataFileDetails { FileType = "N",  FileNumber = 215, NumberOfElements = 255 },
                new DataFileDetails { FileType = "N",  FileNumber = 216, NumberOfElements = 29  },
                new DataFileDetails { FileType = "N",  FileNumber = 217, NumberOfElements = 255 },
                new DataFileDetails { FileType = "N",  FileNumber = 218, NumberOfElements = 255 },
            };
        }

        /// <summary>
        /// Clears the cached list so next call reloads from source.
        /// </summary>
        public static void ClearCache() => _cachedList = null;
    }
}