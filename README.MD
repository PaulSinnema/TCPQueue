TcpQueue

This application communicates with a target one at the time effectively preventing multiple calls to the same target. 

This configuration is for Synology NAS Container Manager.

Use the YAML below to create your Docker project. Change the ports to your own.

version: "3.9"

services:
  tcpqueue:
    container_name: tcpqueue

    # GEBRUIK EEN BESTAANDE IMAGE
    image: 192.168.1.201:5000/tcpqueue:latest
    restart: unless-stopped
    

   # EXPOSE
    expose:
      - "15217"
      - "15240"
      - "15241"
      - "15242"
      - "15243"

    # Ports = publiceren naar jouw Windows host
    ports:
      - "15217:15217"
      - "15240:15240"
      - "15241:15241"
      - "15242:15242"
      - "15243:15243" 
      
    # run als root zodat je config-map leesbaar is
    # user: "0:0"

    environment:
      CONFIG_PATH: "/config"

    volumes:
      - /volume1/containers/tcpqueue/config:/config:ro,z
Put the following appsettings.json on your Synology NAS in the directory specified in the YAML file under Volumes: (in the example: /volume1/containers/tcpqueue:/tcpqueue).

   {
  "TcpQueue": {
    "Forwards": [
      {
        "ListenPort": 15217,
        "Target": "192.168.1.217:1502",
        "Description": "SolarEdge TCP Modbus Inverter"
      },
      {
        "ListenPort": 15240,
        "Target": "192.168.1.240:80",
        "Description": "P1 Meter"
      },
      {
        "ListenPort": 15241,
        "Target": "192.168.1.241:80",
        "Description": "Sessy 1"
      },
      {
        "ListenPort": 15242,
        "Target": "192.168.1.242:80",
        "Description": "Sessy 2"
      },
      {
        "ListenPort": 15243,
        "Target": "192.168.1.243:80",
        "Description": "Sessy 3"
      }
    ]
  },
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "TcpQueueProxy": "Information"
    }
  }
}

