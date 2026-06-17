using System.Xml;

namespace CentrED.Server.Config;

public class UOBridgeConfig
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 2593;
    public string ShardName { get; set; } = "CentrED";
    public int StartX { get; set; } = -1;
    public int StartY { get; set; } = -1;
    public bool SeedLocalClient { get; set; } = true;
    public int MaxClients { get; set; } = 16;

    internal void Write(XmlWriter writer)
    {
        writer.WriteStartElement("UOBridge");
        writer.WriteElementString("Enabled", XmlConvert.ToString(Enabled));
        writer.WriteElementString("Port", XmlConvert.ToString(Port));
        writer.WriteElementString("ShardName", ShardName);
        writer.WriteElementString("StartX", XmlConvert.ToString(StartX));
        writer.WriteElementString("StartY", XmlConvert.ToString(StartY));
        writer.WriteElementString("SeedLocalClient", XmlConvert.ToString(SeedLocalClient));
        writer.WriteElementString("MaxClients", XmlConvert.ToString(MaxClients));
        writer.WriteEndElement();
    }

    internal static UOBridgeConfig Read(XmlReader reader)
    {
        var result = new UOBridgeConfig();
        using XmlReader sub = reader.ReadSubtree();
        sub.Read();
        while (sub.Read())
        {
            if (sub.NodeType == XmlNodeType.Element)
            {
                switch (sub.Name)
                {
                    case "Enabled":
                        result.Enabled = sub.ReadElementContentAsBoolean();
                        break;
                    case "Port":
                        result.Port = sub.ReadElementContentAsInt();
                        break;
                    case "ShardName":
                        result.ShardName = sub.ReadElementContentAsString();
                        break;
                    case "StartX":
                        result.StartX = sub.ReadElementContentAsInt();
                        break;
                    case "StartY":
                        result.StartY = sub.ReadElementContentAsInt();
                        break;
                    case "SeedLocalClient":
                        result.SeedLocalClient = sub.ReadElementContentAsBoolean();
                        break;
                    case "MaxClients":
                        result.MaxClients = sub.ReadElementContentAsInt();
                        break;
                }
            }
        }
        return result;
    }

    public override string ToString()
    {
        return $"{nameof(Enabled)}: {Enabled}, {nameof(Port)}: {Port}, {nameof(ShardName)}: {ShardName}";
    }
}
