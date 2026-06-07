// SPDX-License-Identifier: GPL-3.0-or-later
// 
// PCCCComm - PCCC Communication Library for .NET
// Copyright (c) 2026 Ketut Kumajaya
// 
// Based on original DF1Comm.vb by Archie Jacobs (Manufacturing Automation LLC)
// which was released under GPLv2-or-later.
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

using static PCCCComm.Pccc.PCCCConstants;

namespace PCCCComm.Pccc;

/// <summary>
/// Decodes PCCC status codes (STS and EXT STS) into human-readable messages.
/// Reference: Allen-Bradley Publication 1770-6.5.16, Chapter 8.
/// </summary>
public static class PCCCErrors
{
    /// <summary>
    /// Decodes a status code (STS or STS+EXT STS combination) into a descriptive string.
    /// </summary>
    /// <param name="statusCode">
    /// For normal STS, values 0-255. For extended status (STS=0xF0), pass (256 + extStsByte).
    /// </param>
    /// <returns>Human-readable error description, or "Unknown Message - X" if not recognized.</returns>
    public static string DecodeStatus(int statusCode)
    {
        // Local and remote STS codes (0-255)
        switch (statusCode)
        {
            case Sts.Success: return string.Empty;

            // Custom negative codes used internally by the library (not from PLC)
            case -2: return "Not Acknowledged (NAK)";
            case -3: return "No Response, Check COM Settings";
            case -4: return "Unknown Message from DataLink Layer";
            case -5: return "Invalid Address";
            case -6: return "Could Not Open Com Port";
            case -7: return "No data specified to data link layer";
            case -8: return "No data returned from PLC";
            case -20: return "No Data Returned";
            case -21: return "Received Message NAKd from invalid checksum";

            // Local STS error codes (link layer)
            case Sts.DstOutOfBuffer: return "Destination node is out of buffer space";
            case Sts.CannotGuaranteeDelivery: return "Cannot guarantee delivery, link layer";
            case Sts.DuplicateToken: return "Duplicate token holder detected";
            case Sts.LocalPortDisconnected: return "Local port is disconnected";
            case Sts.AppLayerTimeout: return "Application layer timed out waiting for response";
            case Sts.DuplicateNode: return "Duplicate node detected";
            case Sts.StationOffline: return "Station is offline";
            case Sts.HardwareFault: return "Hardware fault";

            // Remote STS error codes
            case Sts.IllegalCommandOrFormat: return "Illegal Command or Format, Address may not exist or not enough elements in data file";
            case Sts.HostProblem: return "PLC Has a Problem and Will Not Communicate";
            case Sts.RemoteNodeMissing: return "Remote Node Host is Missing, Disconnected, or Shut Down";
            case Sts.HardwareFaultRemote: return "Host Could Not Complete Function Due To Hardware Fault";
            case Sts.AddressingProblem: return "Addressing problem or Memory Protect Rungs";
            case Sts.CommandProtection: return "Function not allowed due to command protection selection";
            case Sts.ProcessorInProgramMode: return "Processor is in Program mode";
            case Sts.CompatibilityModeMissing: return "Compatibility mode file missing or communication zone problem";
            case Sts.RemoteCannotBuffer: return "Remote node cannot buffer command";
            case Sts.WaitAck: return "Wait ACK";
            case Sts.DownloadProblem: return "Remote node problem due to download";
            case Sts.WaitAck2: return "Wait ACK";
            case Sts.ExtStsPresent: break; // handled by EXT STS cases
        }

        // Extended status codes (STS == 0xF0)
        switch (statusCode)
        {
            case ExtSts.IllegalFieldValue: return "A field has an illegal value";
            case ExtSts.LessLevelsThanMinimum: return "Less levels specified in address than minimum for any address";
            case ExtSts.MoreLevelsThanSystemSupports: return "More levels specified in address than system supports";
            case ExtSts.SymbolNotFound: return "Symbol not found";
            case ExtSts.ImproperSymbolFormat: return "Symbol is of improper format";
            case ExtSts.AddressNotUsable: return "Address doesn't point to something usable";
            case ExtSts.FileWrongSize: return "File is wrong size";
            case ExtSts.SituationChanged: return "Cannot complete request, situation has changed since the start of the command";
            case ExtSts.DataOrFileTooLarge: return "Data or file is too large";
            case ExtSts.TransactionSizeTooLarge: return "Transaction size plus word address is too large";
            case ExtSts.AccessDenied: return "Access denied, improper privilege";
            case ExtSts.ResourceNotAvailable: return "Condition cannot be generated - resource is not available";
            case ExtSts.ResourceAlreadyAvailable: return "Condition already exists - resource is already available";
            case ExtSts.CommandCannotBeExecuted: return "Command cannot be executed";
            case ExtSts.HistogramOverflow: return "Histogram overflow";
            case ExtSts.NoAccess: return "No access";
            case ExtSts.IllegalDataType: return "Illegal data type";
            case ExtSts.InvalidParameter: return "Invalid parameter or invalid data";
            case ExtSts.AddressDeleted: return "Address reference exists to deleted area";
            case ExtSts.UnknownFailure: return "Command execution failure for unknown reason";
            case ExtSts.DataConversionError: return "Data conversion error";
            case ExtSts.ScannerCantCommunicate: return "Scanner not able to communicate with 1771 rack adapter";
            case ExtSts.TypeMismatch: return "Type mismatch";
            case ExtSts.ModuleResponseInvalid: return "1771 module response was not valid";
            case ExtSts.DuplicateLabel: return "Duplicate label";
            case ExtSts.FileOpenAnotherNode: return "File is open; another node owns it";
            case ExtSts.ProgramOwnerAnotherNode: return "Another node is the program owner";
            case ExtSts.DataTableProtection: return "Data table element protection violation";
            case ExtSts.TemporaryInternalProblem: return "Temporary internal problem";
            case ExtSts.RemoteRackFault: return "Remote rack fault";
            case ExtSts.Timeout: return "Timeout";
            case ExtSts.UnknownError: return "Unknown error";

            default: return $"Unknown Message - {statusCode}";
        }
    }
}
