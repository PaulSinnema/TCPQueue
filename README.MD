TcpQueue

This application communicates with a target one at the time effectively preventing multiple calls to the same target.
You need this when a target is not multi threaded and gets confused when more than 1 call arrives.

The project is not yet uploaded to Docker Library. Will do that soon. Until then you need to download (pull) the 
sources to your local machine and build it with Visual Studio 2026 (Insiders) or Visual Studio 2022.
You also need the 'registry' container (https://hub.docker.com/_/registry/) to be able to have your local
registry on your Synology NAS (or use the Docker Library) to upload to the registry and download the image into
the Container Manager on your Synology NAS.

In the Properties/PublishProfiles directory there is a 

This configuration is for Synology NAS Container Manager. Use the YAML below to create your Docker project.
Change the IP addresses and ports to your own.

    version: "3.9"

    services:
      tcpqueue:
        container_name: tcpqueue

        # GEBRUIK EEN BESTAANDE IMAGE
        image: 192.168.1.201:5000/tcpqueue:latest   # pas aan als jouw tag anders is
        restart: unless-stopped
        # network_mode: host

       # EXPOSE = alleen intern zichtbaar voor andere containers
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

Put the following appsettings.json on your Synology NAS in the directory specified in the YAML file under Volumes:
(in the example: /volume1/containers/tcpqueue:/tcpqueue).

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
        "Target": "192.168.1.240:80", // Don't remove the ':80', it will crash the application
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
      "TcpQueueProxy": "Warning"
    }
  }
}

