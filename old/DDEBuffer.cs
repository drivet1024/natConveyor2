using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Nat.Utils;
using System.Threading;

namespace Nat_Conveyor
{

    //public class DDEInterface
    //{
    //    TcpClient client = new TcpClient();
    //    NDde.Client.DdeClient ddeClient;

    //    public void ConnectIP(string _serverIP, int _port)
    //    {
    //        client.Connect(_serverIP,_port);
            
    //    }

    //    public void Connect()
    //    {
    //        ddeClient = new NDde.Client.DdeClient("RsLinx", "NATIONEX");
    //        try
    //        {
    //          ddeClient.Connect();
    //        }
    //        catch
    //        {
    //            Thread.Sleep(100);
    //        }
    //    }


    //    public string RequestIP(string command)
    //    {
            
    //        Byte[] bytes = new Byte[256];
    //         TcpClient client = new TcpClient();
    //        string ret = "";
    //        client.ReceiveTimeout = 3000;
    //        //client.Connect(Properties.Settings.Default.DDE_server, Properties.Settings.Default.DDE_port_out);

    //        try
    //        {
    //            var s = client.GetStream();
    //            string buffer = "r;" + command + ";";
    //            s.Write(Encoding.UTF8.GetBytes(buffer), 0, buffer.Length);

    //            int i = s.Read(bytes, 0, bytes.Length);
    //            ret = System.Text.Encoding.UTF8.GetString(bytes, 0, i);
    //            ret = ret.Replace("\0", "");
    //        }
    //        catch (Exception ex)
    //        {

    //        }
    //        //client.Close();

    //        return ret;

    //    }

    //    public string Request(string command)
    //    {
    //        string ret = ddeClient.Request(command, 500);
    //        //Byte[] bytes = new Byte[256];
    //        // TcpClient client = new TcpClient();
    //        //string ret = "";
    //        //client.ReceiveTimeout = 3000;
    //        //client.Connect(Properties.Settings.Default.DDE_server, Properties.Settings.Default.DDE_port_out);

    //        //try
    //        //{
    //        //    var s = client.GetStream();
    //        //    string buffer = "r;" + command +";" ;
    //        //    s.Write(Encoding.UTF8.GetBytes(buffer), 0, buffer.Length);

    //        //    i = s.Read(bytes, 0, bytes.Length);
    //        //    ret = System.Text.Encoding.UTF8.GetString(bytes, 0, i);
    //        //    ret = ret.Replace("\0","");
    //        //}
    //        //catch (Exception ex)
    //        //{

    //        //}
    //        //client.Close();
    //        return ret;

    //    }
    //    public bool IsDDeConnected()
    //    {
    //        //Byte[] bytes = new Byte[256];

    //        //TcpClient client = new TcpClient();
    //        //bool ret = false;
    //        //client.ReceiveTimeout = 3000;
    //        //try
    //        //{
    //        //    client.Connect(Properties.Settings.Default.DDE_server, Properties.Settings.Default.DDE_port_out);
    //        //    var s = client.GetStream();
    //        //    string buffer = "c;;";
    //        //    s.Write(Encoding.UTF8.GetBytes(buffer), 0, buffer.Length);

    //        //    i = s.Read(bytes, 0, bytes.Length);
    //        //    string r = System.Text.Encoding.UTF8.GetString(bytes, 0, i);
    //        //    ret = r.Replace("\0", "").ToBoolFalseDefault();
    //        //}
    //        //catch (Exception ex)
    //        //{

    //        //}
    //        //client.Close();

    //        return ddeClient.IsConnected;

    //    }
    //    public void PokeIP(string command, string data)
    //    {
    //        //TcpClient client = new TcpClient();

    //        //client.Connect(Properties.Settings.Default.DDE_server, Properties.Settings.Default.DDE_port_out) ;
    //        var s = client.GetStream();
    //        string buffer = "p;"+command + ";"+data;
    //        s.Write(Encoding.UTF8.GetBytes(buffer), 0, buffer.Length);

    //        //client.Close();

    //    }
    //    public void Poke(string command, string data)
    //    {
    //        try
    //        {

    //           // if (!ddeClient.IsConnected)
    //          //      ddeClient.Connect();

    //            ddeClient.Poke(command, data, 300);
    //        }
    //        catch
    //        {

    //        }

    //        //TcpClient client = new TcpClient();

    //        //client.Connect(Properties.Settings.Default.DDE_server, Properties.Settings.Default.DDE_port_out) ;
    //        //var s = client.GetStream();
    //        //string buffer = "p;"+command + ";"+data;
    //        //s.Write(Encoding.UTF8.GetBytes(buffer), 0, buffer.Length);

    //        //client.Close();
    //    }

    //}
}
