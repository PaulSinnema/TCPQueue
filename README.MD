TcpQueue

This configuration is for Synology NAS Container Manager.

This application communicates with a target one at the time effectively preventing multiple calls to the same target. 

Use the YAML below to create your Docker project. Change the ports to your own.

    version: "3.9"

    services:
      tcpqueue:
        container_name: tcpqueue

        # GEBRUIK EEN BESTAANDE IMAGE
        image: 192.168.1.201:5000/tcpqueue:latest   # pas aan als jouw tag anders is
        restart: unless-stopped

       # EXPOSE ports for the container
        expose:
          - "15217"
          - "15240"
          - "15241"
          - "15242"
          - "15243"

        # Port mappings
        ports:
          - "15217:15217"
          - "15240:15240"
          - "15241:15241"
          - "15242:15242"
          - "15243:15243" 
      
        # run as root
        user: "0:0"

        environment:
          CONFIG_PATH: "/tcpqueue/config"

        volumes:
          - /volume1/containers/tcpqueue:/tcpqueue

Put the following appsettings.json on your Synology NAS in the directory specified in the YAML file under Volumes: (in the example: /volume1/containers/tcpqueue:/tcpqueue).

    {
      "TcpQueue": {
        "SessionTimeoutSeconds": 120,
        "Listeners": [
          {
            "ListenPort": 15217,
            "TargetHost": "192.168.1.217",
            "TargetPort": 80,
            "Description": "SolarEdge TCP Modbus Inverter"
          },
          {
            "ListenPort": 15240,
            "TargetHost": "192.168.1.240",
            "TargetPort": 80,
            "Description": "P1 Meter"
          },
          {
            "ListenPort": 15241,
            "TargetHost": "192.168.1.241",
            "TargetPort": 80,
            "Description": "Sessy 1"
          },
          {
            "ListenPort": 15242,
            "TargetHost": "192.168.1.242",
            "TargetPort": 80,
            "Description": "Sessy 2"
          },
          {
            "ListenPort": 15243,
            "TargetHost": "192.168.1.243",
            "TargetPort": 80,
            "Description": "Sessy 3"
          }
        ]
      },
      "Logging": {
        "LogLevel": {
          "Default": "Error",
          "Microsoft.Hosting.Lifetime": "Information",
          "Microsoft": "Warning"
        }
      }
    }
