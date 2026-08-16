pipeline {
    agent any

    options {
        timestamps()
        disableConcurrentBuilds()
        timeout(time: 30, unit: 'MINUTES')
        buildDiscarder(logRotator(numToKeepStr: '20', artifactNumToKeepStr: '10'))
    }

    triggers {
        pollSCM('H/2 * * * *')
    }

    environment {
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_NOLOGO = '1'
        NUGET_PACKAGES = "${WORKSPACE}/.nuget/packages"
        RELEASE_ARCHIVE = "rise-${BUILD_NUMBER}.tar.gz"
    }

    stages {
        stage('Checkout') {
            steps {
                checkout scm
                sh 'git log -1 --pretty=fuller'
            }
        }

        stage('Initialise') {
            steps {
                script {
                    env.DEPLOY_TARGET = env.JOB_BASE_NAME.endsWith('-cloud') ? 'cloud' : 'local'
                    env.DEPLOY_HOST = env.DEPLOY_TARGET == 'cloud' ? env.CLOUD_APP_HOST : env.LOCAL_APP_HOST

                    if (!env.DEPLOY_HOST?.trim()) {
                        error("No application host configured for ${env.DEPLOY_TARGET}")
                    }

                    currentBuild.displayName = "#${env.BUILD_NUMBER} ${env.GIT_COMMIT?.take(8) ?: 'checkout'} -> ${env.DEPLOY_TARGET}"
                }

                sh '''
                    set -eu
                    umask 077
                    : > "$WORKSPACE/.known_hosts"

                    if [ "$DEPLOY_TARGET" = "local" ]; then
                      test -s /vagrant/.generated/app-known-hosts
                      cat /vagrant/.generated/app-known-hosts >> "$WORKSPACE/.known_hosts"
                    else
                      test -s "$HOME/.ssh/cloud-known-hosts"
                      cat "$HOME/.ssh/cloud-known-hosts" >> "$WORKSPACE/.known_hosts"
                    fi

                    ssh-keygen -F "$DEPLOY_HOST" -f "$WORKSPACE/.known_hosts" >/dev/null
                '''
            }
        }

        stage('Restore') {
            steps {
                sh 'dotnet restore Rise.sln --disable-parallel -m:1 -p:RestoreUseStaticGraphEvaluation=true'
            }
        }

        stage('Static analysis') {
            parallel {
                stage('Analyzers') {
                    steps {
                        sh 'dotnet format analyzers Rise.sln --verify-no-changes --no-restore --severity error --verbosity minimal'
                    }
                }
                stage('Dependencies') {
                    steps {
                        sh '''
                            dotnet list Rise.sln package \
                              --vulnerable \
                              --include-transitive \
                              --format json > vulnerable-packages.json
                            cat vulnerable-packages.json
                            test "$(jq '[.. | objects | .vulnerabilities? // empty | .[]] | length' vulnerable-packages.json)" -eq 0
                        '''
                        archiveArtifacts artifacts: 'vulnerable-packages.json', fingerprint: true
                    }
                }
            }
        }

        stage('Build') {
            steps {
                sh 'dotnet build Rise.sln --configuration Release --no-restore --nologo -m:1 /p:ContinuousIntegrationBuild=true'
            }
        }

        stage('Test') {
            steps {
                sh '''
                    rm -rf TestResults
                    dotnet test Rise.sln \
                      --configuration Release \
                      --no-build \
                      -m:1 \
                      --logger trx \
                      --results-directory TestResults \
                      --collect:"XPlat Code Coverage"
                '''
            }
            post {
                always {
                    mstest testResultsFile: 'TestResults/**/*.trx', keepLongStdio: true
                    archiveArtifacts artifacts: 'TestResults/**', allowEmptyArchive: true
                }
            }
        }

        stage('Publish') {
            steps {
                sh '''
                    rm -rf publish "$RELEASE_ARCHIVE"
                    dotnet publish src/Rise.Server/Rise.Server.csproj \
                      --configuration Release \
                      --no-restore \
                      --output publish \
                      -m:1 \
                      /p:UseAppHost=false
                    tar -C publish -czf "$RELEASE_ARCHIVE" .
                    sha256sum "$RELEASE_ARCHIVE" > "$RELEASE_ARCHIVE.sha256"
                '''
                archiveArtifacts artifacts: 'rise-*.tar.gz,rise-*.tar.gz.sha256', fingerprint: true
            }
        }

        stage('Deploy') {
            steps {
                sshagent(credentials: ['rise-deploy-key']) {
                    sh '''
                        set -eu
                        ssh -o BatchMode=yes -o StrictHostKeyChecking=yes \
                          -o "UserKnownHostsFile=$WORKSPACE/.known_hosts" \
                          "rise-deploy@$DEPLOY_HOST" true
                        scp -o BatchMode=yes -o StrictHostKeyChecking=yes \
                          -o "UserKnownHostsFile=$WORKSPACE/.known_hosts" \
                          "$RELEASE_ARCHIVE" "rise-deploy@$DEPLOY_HOST:/tmp/$RELEASE_ARCHIVE"
                        ssh -o BatchMode=yes -o StrictHostKeyChecking=yes \
                          -o "UserKnownHostsFile=$WORKSPACE/.known_hosts" \
                          "rise-deploy@$DEPLOY_HOST" \
                          "sudo /usr/local/sbin/deploy-rise '/tmp/$RELEASE_ARCHIVE' '$BUILD_NUMBER'"
                    '''
                }
            }
        }

        stage('Smoke test') {
            steps {
                retry(5) {
                    sleep 3
                    sh 'curl --fail --silent --show-error --insecure "https://$DEPLOY_HOST/health/ready"'
                }
            }
        }
    }

    post {
        success {
            echo "Revision ${env.GIT_COMMIT} is live on ${env.DEPLOY_TARGET}."
        }
        cleanup {
            deleteDir()
        }
    }
}
